using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AnisePOS.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Notifications;

public sealed record IncomingCallerEvent(Guid EventId, string? CallerNumber, DateTimeOffset Timestamp, string EventType);
public sealed record CallerNotification(Guid EventId, DateTimeOffset Timestamp, string? CallerNumber, Guid? CustomerId, string? CustomerName);

public sealed class CallerEventBroker
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> seen = new();
    private readonly HashSet<Channel<CallerNotification>> listeners = new();

    public bool Reserve(Guid id, DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var old in seen.Where(x => x.Value < now.AddMinutes(-10)).Select(x => x.Key).ToArray()) seen.Remove(old);
            return seen.TryAdd(id, now);
        }
    }

    public void Release(Guid id) { lock (gate) seen.Remove(id); }

    public ChannelReader<CallerNotification> Subscribe(out Channel<CallerNotification> channel)
    {
        channel = Channel.CreateBounded<CallerNotification>(new BoundedChannelOptions(32)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        lock (gate) listeners.Add(channel);
        return channel.Reader;
    }

    public void Unsubscribe(Channel<CallerNotification> channel)
    {
        lock (gate) listeners.Remove(channel);
        channel.Writer.TryComplete();
    }

    public void Publish(CallerNotification notification)
    {
        lock (gate) foreach (var listener in listeners) listener.Writer.TryWrite(notification);
    }
}

public static class CallerEventEndpoints
{
    public static bool Authorized(string? supplied, string? configured)
    {
        if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(configured) || configured.Length < 32) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(configured)));
    }

    public static string? NormalizePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("withheld", StringComparison.OrdinalIgnoreCase) ||
            value.Trim().Equals("unknown", StringComparison.OrdinalIgnoreCase)) return null;
        var compact = new string(value.Where(c => c is not (' ' or '-' or '(' or ')')).ToArray());
        if (compact.Length < 7 || compact.Length > 21 || compact.Skip(compact[0] == '+' ? 1 : 0).Any(c => !char.IsAsciiDigit(c))) return null;
        if (compact.StartsWith("0044")) return "0" + compact[4..].TrimStart('0');
        if (compact.StartsWith("+44")) return "0" + compact[3..].TrimStart('0');
        if (compact.StartsWith("00")) return "+" + compact[2..];
        return compact;
    }

    public static bool Valid(IncomingCallerEvent? value, DateTimeOffset now) => value is not null &&
        value.EventId != Guid.Empty && value.EventType == "incoming/ringing" &&
        value.Timestamp >= now.AddMinutes(-2) && value.Timestamp <= now.AddSeconds(30) &&
        (value.CallerNumber is null || value.CallerNumber.Length <= 30) &&
        (value.CallerNumber is null || value.CallerNumber.Equals("Withheld", StringComparison.OrdinalIgnoreCase) ||
         value.CallerNumber.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || NormalizePhone(value.CallerNumber) is not null);

    public static T? FindCustomer<T>(IEnumerable<T> candidates, string phone, Func<T, string?> getPhone) where T : class =>
        candidates.FirstOrDefault(candidate => NormalizePhone(getPhone(candidate)) == phone);

    public static void MapCallerEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/caller/events", async (HttpContext context, IncomingCallerEvent? request,
            IConfiguration config, AniseDbContext db, CallerEventBroker broker, CancellationToken ct) =>
        {
            if (!context.Request.IsHttps) return Results.StatusCode(403);
            var auth = context.Request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                !Authorized(auth[7..], config["CallerId:IngestSecret"])) return Results.Unauthorized();
            var now = DateTimeOffset.UtcNow;
            if (!Valid(request, now)) return Results.BadRequest(new { message = "Invalid caller event." });
            if (!broker.Reserve(request!.EventId, now)) return Results.Ok(new { accepted = true, duplicate = true });
            try
            {
                var phone = NormalizePhone(request.CallerNumber);
                Guid? customerId = null;
                string? customerName = null;
                if (phone is not null)
                {
                    // Customer.Phone is free text. Narrow candidates in SQL, then compare canonical numbers in .NET.
                    var suffix = phone.Length >= 7 ? phone[^7..] : phone;
                    var candidates = await db.Customers.AsNoTracking()
                        .Where(c => c.Phone != null && c.Phone.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").EndsWith(suffix))
                        .OrderByDescending(c => c.UpdatedAtUtc ?? c.CreatedAtUtc)
                        .Select(c => new { c.Id, c.FirstName, c.LastName, c.Phone }).Take(100).ToListAsync(ct);
                    var match = FindCustomer(candidates, phone, c => c.Phone);
                    if (match is not null)
                    {
                        customerId = match.Id;
                        customerName = string.Join(" ", new[] { match.FirstName, match.LastName }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    }
                }
                broker.Publish(new CallerNotification(request.EventId, request.Timestamp, phone, customerId, customerName));
                return Results.Accepted(value: new { accepted = true, duplicate = false });
            }
            catch { broker.Release(request.EventId); throw; }
        }).RequireRateLimiting("caller-events").DisableAntiforgery();

        app.MapGet("/api/caller/stream", async (HttpContext context, CallerEventBroker broker) =>
        {
            if (!context.Request.IsHttps) { context.Response.StatusCode = 403; return; }
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            var reader = broker.Subscribe(out var channel);
            try
            {
                await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                    heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                    bool available;
                    try { available = await reader.WaitToReadAsync(heartbeat.Token); }
                    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
                    { await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted); await context.Response.Body.FlushAsync(context.RequestAborted); continue; }
                    if (!available) break;
                    while (reader.TryRead(out var item))
                    {
                        if (item.Timestamp < DateTimeOffset.UtcNow.AddMinutes(-2)) continue;
                        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(item) + "\n\n", context.RequestAborted);
                    }
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            finally { broker.Unsubscribe(channel); }
        }).RequireAuthorization();
    }
}

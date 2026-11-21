using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnisePOS.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Notifications;

public sealed record IncomingCallerEvent(Guid EventId, string? CallerNumber, DateTimeOffset Timestamp, string EventType);
public sealed record CallerNotification(Guid EventId, DateTimeOffset Timestamp, string? CallerNumber, Guid? CustomerId, string? CustomerName);

public static class CallerEventEndpoints
{
    public static string SerializeStreamNotification(CallerNotification notification) =>
        JsonSerializer.Serialize(notification, new JsonSerializerOptions(JsonSerializerDefaults.Web));

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
        app.MapGet("/api/caller/recent", async (long? after, CallerEventStore store, CancellationToken ct) =>
            Results.Ok(await store.ReadAfterAsync(Math.Max(0, after ?? 0), ct))).RequireAuthorization();

        app.MapGet("/api/caller/customer", async (string phone, AniseDbContext db, CancellationToken ct) =>
        {
            var normalized = NormalizePhone(phone);
            if (normalized is null) return Results.BadRequest();
            var suffix = normalized[^7..];
            var candidates = await db.Customers.AsNoTracking().Include(c => c.Addresses)
                .Where(c => c.Phone != null && c.Phone.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").EndsWith(suffix))
                .OrderByDescending(c => c.UpdatedAtUtc ?? c.CreatedAtUtc).Take(100).ToListAsync(ct);
            var customer = FindCustomer(candidates, normalized, c => c.Phone);
            var address = customer?.Addresses.Where(a => !string.IsNullOrWhiteSpace(a.Line1))
                .OrderByDescending(a => a.IsDefault).ThenByDescending(a => a.CreatedAtUtc).FirstOrDefault();
            var lastOrder = customer is null ? null : await db.Orders.AsNoTracking()
                .Where(o => o.CustomerId == customer.Id).OrderByDescending(o => o.CreatedAtUtc)
                .Select(o => (DateTimeOffset?)o.CreatedAtUtc).FirstOrDefaultAsync(ct);
            return Results.Ok(new { existing = customer is not null,
                name = customer is null ? null : string.Join(" ", new[] { customer.FirstName, customer.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))),
                address = address is null ? null : string.Join(", ", new[] { address.Line1, address.Line2, address.City, address.Postcode }.Where(x => !string.IsNullOrWhiteSpace(x))),
                lastOrder });
        }).RequireAuthorization();
        app.MapPost("/api/caller/events", async (HttpContext context, IncomingCallerEvent? request,
            IConfiguration config, AniseDbContext db, CallerEventStore store, CancellationToken ct) =>
        {
            if (!context.Request.IsHttps) return Results.StatusCode(403);
            var auth = context.Request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                !Authorized(auth[7..], config["CallerId:IngestSecret"])) return Results.Unauthorized();
            var now = DateTimeOffset.UtcNow;
            if (!Valid(request, now)) return Results.BadRequest(new { message = "Invalid caller event." });
            var callerEvent = request!;
            var phone = NormalizePhone(callerEvent.CallerNumber);
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
            if (!await store.InsertAsync(new CallerNotification(callerEvent.EventId, callerEvent.Timestamp, phone, customerId, customerName), ct))
                return Results.Ok(new { accepted = true, duplicate = true });
            return Results.Accepted(value: new { accepted = true, duplicate = false });
        }).RequireRateLimiting("caller-events").DisableAntiforgery();

        app.MapGet("/api/caller/stream", async (HttpContext context, CallerEventStore store, IConfiguration config) =>
        {
            if (!context.Request.IsHttps) { context.Response.StatusCode = 403; return; }
            if (config["CallerId:IngestSecret"]?.Length < 32 || config["CallerId:IngestSecret"] is null)
            { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            var lastEventId = context.Request.Headers["Last-Event-ID"].ToString();
            long sequence = long.TryParse(lastEventId, out var remembered) && remembered >= 0
                ? remembered : await store.LatestSequenceAsync(context.RequestAborted);
            try
            {
                await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                var lastHeartbeat = DateTimeOffset.UtcNow;
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    var events = await store.ReadAfterAsync(sequence, context.RequestAborted);
                    foreach (var item in events)
                    {
                        sequence = item.Sequence;
                        await context.Response.WriteAsync("id: " + sequence + "\ndata: " + SerializeStreamNotification(item.Notification) + "\n\n", context.RequestAborted);
                    }
                    if (events.Count > 0) await context.Response.Body.FlushAsync(context.RequestAborted);
                    if (DateTimeOffset.UtcNow - lastHeartbeat >= TimeSpan.FromSeconds(15))
                    {
                        await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        lastHeartbeat = DateTimeOffset.UtcNow;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), context.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        }).RequireAuthorization();
    }
}

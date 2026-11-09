using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnisePOS.Api.Data;
using AnisePOS.Api.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WebPush;

namespace AnisePOS.Api.Notifications;

public sealed record StaffPushKeys(string P256dh, string Auth);
public sealed record StaffPushRegistration(string Endpoint, StaffPushKeys Keys);

public sealed class PushKeyStore(IWebHostEnvironment environment, IDataProtectionProvider protection)
{
    private readonly object gate = new();
    private VapidDetails? keys;
    public VapidDetails Get()
    {
        lock (gate)
        {
            if (keys is not null) return keys;
            var directory = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "data"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "staff-push-keys.protected");
            var protector = protection.CreateProtector("Anise.StaffPush.Keys.v1");
            if (!File.Exists(path))
            {
                var generated = VapidHelper.GenerateVapidKeys();
                var encrypted = protector.Protect(JsonSerializer.Serialize(generated));
                try { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); using var writer = new StreamWriter(file); writer.Write(encrypted); }
                catch (IOException) when (File.Exists(path)) { }
            }
            keys = JsonSerializer.Deserialize<VapidDetails>(protector.Unprotect(File.ReadAllText(path))) ?? throw new InvalidOperationException("Push keys unavailable.");
            keys.Subject = "mailto:info@loveanise.co.uk";
            return keys;
        }
    }
}

public static class StaffPushEndpoints
{
    public static bool ValidEndpoint(string? endpoint)
    {
        if (endpoint is null || endpoint.Length > 2048 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        var host = uri.IdnHost.ToLowerInvariant();
        return host.EndsWith(".push.apple.com", StringComparison.Ordinal) || host == "fcm.googleapis.com" || host == "updates.push.services.mozilla.com" || host.EndsWith(".notify.windows.com", StringComparison.Ordinal);
    }
    private static bool ValidKey(string? value, int length)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 150 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        try { return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4)).Length == length; } catch (FormatException) { return false; }
    }
    private static string Hash(string endpoint) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));
    public static void MapStaffPushEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/staff/push").RequireAuthorization();
        group.MapGet("/key", (PushKeyStore store) => Results.Ok(new { publicKey = store.Get().PublicKey }));
        group.MapPost("/status", async (StaffPushRegistration? request, ClaimsPrincipal user, AniseDbContext db, CancellationToken ct) =>
        {
            if (request?.Endpoint is null || !ValidEndpoint(request.Endpoint)) return Results.BadRequest();
            var hash = Hash(request.Endpoint); var staffId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            var enabled = await db.StaffPushSubscriptions.AnyAsync(x => x.EndpointHash == hash && x.StaffId == staffId, ct);
            return Results.Ok(new { enabled });
        });
        group.MapPost("/subscribe", async (StaffPushRegistration? request, ClaimsPrincipal user, AniseDbContext db, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            var staffId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (staffId is null || request?.Keys is null || !ValidEndpoint(request.Endpoint) || !ValidKey(request.Keys.P256dh, 65) || !ValidKey(request.Keys.Auth, 16)) return Results.BadRequest(new { message = "Invalid notification subscription." });
            var hash = Hash(request.Endpoint);
            var row = await db.StaffPushSubscriptions.SingleOrDefaultAsync(x => x.EndpointHash == hash, ct);
            if (row is null) { row = new StaffPushSubscription { EndpointHash = hash }; db.StaffPushSubscriptions.Add(row); }
            row.StaffId = staffId; row.ProtectedSubscription = protection.CreateProtector("Anise.StaffPush.Subscription.v1").Protect(JsonSerializer.Serialize(request));
            row.CreatedAtUtc = DateTimeOffset.UtcNow; row.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct); return Results.Ok();
        });
        group.MapPost("/unsubscribe", async (StaffPushRegistration? request, ClaimsPrincipal user, AniseDbContext db, CancellationToken ct) =>
        {
            if (request?.Endpoint is null || !ValidEndpoint(request.Endpoint)) return Results.BadRequest();
            var hash = Hash(request.Endpoint); var staffId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            await db.StaffPushSubscriptions.Where(x => x.EndpointHash == hash && x.StaffId == staffId).ExecuteDeleteAsync(ct); return Results.Ok();
        });
    }
}

public sealed class StaffPushWorker(IServiceScopeFactory scopes, PushKeyStore keys, IDataProtectionProvider protection, ILogger<StaffPushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Process(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError("Staff push processing failed ({ErrorType})", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
    private async Task Process(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AniseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var stale = now.AddMinutes(-3);
        await db.StaffPushDeliveries.Where(x => x.State == "Sending" && x.UpdatedAtUtc < stale).ExecuteUpdateAsync(x => x.SetProperty(y => y.State, "Pending"), ct);
        var subscriptions = await db.StaffPushSubscriptions.AsNoTracking().ToListAsync(ct);
        foreach (var sub in subscriptions)
        {
            if (!Guid.TryParse(sub.StaffId, out var staffId) || !await db.StaffUsers.AnyAsync(x => x.Id == staffId && x.IsActive, ct)) continue;
            var cutoff = sub.CreatedAtUtc > now.AddDays(-1) ? sub.CreatedAtUtc : now.AddDays(-1);
            var missing = await db.Orders.AsNoTracking().Where(o => o.CreatedAtUtc >= cutoff && o.Status == OrderStatus.Placed &&
                o.Payments.Any(p => p.Provider == "WebsiteCash" || p.Provider == "StripeCheckout") &&
                !db.StaffPushDeliveries.Any(d => d.SubscriptionId == sub.Id && d.OrderId == o.Id)).Select(o => o.Id).Take(30).ToListAsync(ct);
            foreach (var id in missing) db.StaffPushDeliveries.Add(new StaffPushDelivery { SubscriptionId = sub.Id, OrderId = id });
            try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        }
        var deliveries = await db.StaffPushDeliveries.AsNoTracking().Where(x => x.State == "Pending" && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now) && x.CreatedAtUtc >= now.AddDays(-1)).OrderBy(x => x.CreatedAtUtc).Take(30).ToListAsync(ct);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        using var client = new WebPushClient(http);
        foreach (var item in deliveries)
        {
            var claimed = await db.StaffPushDeliveries.Where(x => x.Id == item.Id && x.State == "Pending").ExecuteUpdateAsync(x => x.SetProperty(y => y.State, "Sending").SetProperty(y => y.UpdatedAtUtc, DateTimeOffset.UtcNow), ct);
            if (claimed != 1) continue;
            var subscription = await db.StaffPushSubscriptions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == item.SubscriptionId, ct);
            var stillAwaiting = await db.Orders.AnyAsync(x => x.Id == item.OrderId && x.Status == OrderStatus.Placed, ct);
            var staffActive = subscription is not null && Guid.TryParse(subscription.StaffId, out var recipientId) && await db.StaffUsers.AnyAsync(x => x.Id == recipientId && x.IsActive, ct);
            if (subscription is null || !stillAwaiting || !staffActive) { await db.StaffPushDeliveries.Where(x => x.Id == item.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.State, "Skipped"), ct); continue; }
            try
            {
                var data = JsonSerializer.Deserialize<StaffPushRegistration>(protection.CreateProtector("Anise.StaffPush.Subscription.v1").Unprotect(subscription.ProtectedSubscription)) ?? throw new InvalidOperationException();
                if (!StaffPushEndpoints.ValidEndpoint(data.Endpoint)) throw new InvalidOperationException();
                var options = new Dictionary<string, object> { ["vapidDetails"] = keys.Get(), ["TTL"] = 300 };
                var payload = JsonSerializer.Serialize(new { title = "New website order", body = "A new order is awaiting acceptance. Tap to open the queue.", tag = "anise-order-" + item.OrderId.ToString("N") });
                await client.SendNotificationAsync(new PushSubscription(data.Endpoint, data.Keys.P256dh, data.Keys.Auth), payload, options, ct);
                await db.StaffPushDeliveries.Where(x => x.Id == item.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.State, "Sent").SetProperty(y => y.UpdatedAtUtc, DateTimeOffset.UtcNow), ct);
            }
            catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
            {
                await db.StaffPushSubscriptions.Where(x => x.Id == item.SubscriptionId).ExecuteDeleteAsync(ct);
                await db.StaffPushDeliveries.Where(x => x.Id == item.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.State, "Expired"), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Staff push delivery failed ({ErrorType})", ex.GetType().Name);
                await db.StaffPushDeliveries.Where(x => x.Id == item.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Attempts, y => y.Attempts + 1).SetProperty(y => y.State, item.Attempts >= 5 ? "Failed" : "Pending").SetProperty(y => y.NextAttemptAtUtc, DateTimeOffset.UtcNow.AddMinutes(1)).SetProperty(y => y.UpdatedAtUtc, DateTimeOffset.UtcNow), ct);
            }
        }
        await db.StaffPushDeliveries.Where(x => x.CreatedAtUtc < now.AddDays(-7)).ExecuteDeleteAsync(ct);
    }
}

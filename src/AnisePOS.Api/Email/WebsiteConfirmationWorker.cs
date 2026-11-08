using System.Text.Json;
using AnisePOS.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Email;

public sealed record ConfirmationPayload(string Email, string TrackingUrl);

public sealed class WebsiteConfirmationWorker(IServiceScopeFactory scopes, GmailConfirmationSender sender,
    IDataProtectionProvider protection, ILogger<WebsiteConfirmationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (sender.IsConfigured) await ProcessOne(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogError(e, "Website confirmation queue processing failed"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task ProcessOne(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AniseDbContext>();
        var expired = DateTimeOffset.UtcNow.AddDays(-7);
        await db.WebsiteConfirmations.Where(x => x.Kind == "Received" && x.State == "Sent" && x.CreatedAtUtc < expired && x.ProtectedPayload != "")
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.ProtectedPayload, ""), ct);
        var stale = DateTimeOffset.UtcNow.AddMinutes(-10);
        await db.WebsiteConfirmations.Where(x => x.State == "Sending" && x.UpdatedAtUtc < stale)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.State, "NeedsReview").SetProperty(y => y.UpdatedAtUtc, DateTimeOffset.UtcNow), ct);
        var pending = await db.WebsiteConfirmations.AsNoTracking().Where(x => x.State == "Pending")
            .OrderBy(x => x.CreatedAtUtc).Select(x => x.Id).FirstOrDefaultAsync(ct);
        if (pending == Guid.Empty) return;
        // A single worker claims the message. An interrupted/ambiguous send is not retried automatically.
        var claimed = await db.WebsiteConfirmations.Where(x => x.Id == pending && x.State == "Pending")
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.State, "Sending").SetProperty(y => y.UpdatedAtUtc, DateTimeOffset.UtcNow), ct);
        if (claimed != 1) return;
        var item = await db.WebsiteConfirmations.Include(x => x.Order).SingleAsync(x => x.Id == pending, ct);
        try
        {
            var payload = JsonSerializer.Deserialize<ConfirmationPayload>(protection.CreateProtector("Anise.WebsiteConfirmation.v1").Unprotect(item.ProtectedPayload))
                ?? throw new InvalidOperationException("Confirmation payload missing.");
            await sender.SendAsync(payload.Email, item.Order.OrderNumber, payload.TrackingUrl, item.Kind, item.Order.OrderType, item.Order.EstimatedAtUtc, ct);
            item.State = "Sent"; item.SentAtUtc = DateTimeOffset.UtcNow;
            if (item.Kind == "Accepted") item.ProtectedPayload = "";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            item.State = "NeedsReview";
            logger.LogError("Confirmation delivery for order {OrderNumber} needs review ({ErrorType})", item.Order.OrderNumber, e.GetType().Name);
        }
        item.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

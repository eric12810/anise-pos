using System.Net.Http.Headers;
using System.Text.Json;
using AnisePOS.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Endpoints;

// Winserve's scheduled keep-alive must keep the IIS application running for this worker.
public sealed class WebsiteStripeReconciliationWorker(IServiceScopeFactory scopes, IConfiguration config,
    IHostEnvironment environment, IHttpClientFactory clients, ILogger<WebsiteStripeReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileOnce(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Stripe checkout reconciliation failed; staff review required"); }
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }

    private async Task ReconcileOnce(CancellationToken ct)
    {
        if (!config.GetValue("WebsiteCheckout:StripeSessionsEnabled", false)) return;
        var mode = WebsiteStripeMode.From(config, environment);
        if (mode is null) { logger.LogError("Stripe reconciliation disabled: environment configuration mismatch"); return; }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AniseDbContext>();
        var pending = await db.WebsiteCheckouts.AsNoTracking()
            .Where(c => c.CompletedOrderId == null && c.CreatedAtUtc > DateTimeOffset.UtcNow.AddDays(-30) &&
                c.CreatedAtUtc < DateTimeOffset.UtcNow.AddMinutes(-2) &&
                (c.ReconciliationCheckedAtUtc == null || c.ReconciliationCheckedAtUtc < DateTimeOffset.UtcNow.AddMinutes(-10)))
            .OrderBy(c => c.ReconciliationCheckedAtUtc).ThenBy(c => c.CreatedAtUtc).Take(25)
            .Select(c => new { c.Id, c.StripeSessionId, c.CreatedAtUtc }).ToListAsync(ct);
        foreach (var checkout in pending)
        {
            try
            {
                var session = checkout.StripeSessionId is { } id
                    ? await GetStripeJson("checkout/sessions/" + Uri.EscapeDataString(id), ct)
                    : await FindSession(checkout.Id, checkout.CreatedAtUtc, ct);
                if (session is null) continue;
                using (session)
                {
                    var root = session.RootElement;
                    if (!TryPaidSession(root, checkout.Id, mode, out var paid))
                    {
                        if (root.TryGetProperty("payment_status", out var status) && status.GetString() == "paid")
                            await RecordAlert(db, checkout.Id, "Paid Stripe session failed identity or amount checks", ct);
                        continue;
                    }
                    if (checkout.StripeSessionId is null)
                    {
                        var claimed = await db.WebsiteCheckouts.Where(c => c.Id == checkout.Id && c.StripeSessionId == null)
                            .ExecuteUpdateAsync(s => s.SetProperty(c => c.StripeSessionId, paid!.SessionId), ct);
                        if (claimed != 1) continue;
                    }
                    var result = await WebsiteStripeWebhook.FulfilPaidSessionAsync(paid!, db,
                        logger, ct);
                    if (result is not Microsoft.AspNetCore.Http.HttpResults.Ok)
                    {
                        await RecordAlert(db, checkout.Id, "Paid Stripe session could not create an order", ct);
                        logger.LogError("Paid Stripe checkout {CheckoutId} needs staff review; fulfilment returned {ResultType}", checkout.Id, result.GetType().Name);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Stripe checkout {CheckoutId} needs staff review", checkout.Id);
                try { await RecordAlert(db, checkout.Id, "Stripe checkout reconciliation failed; check payment in Stripe", ct); }
                catch (Exception recordError) { logger.LogError(recordError, "Could not record Stripe checkout alert {CheckoutId}", checkout.Id); }
            }
            finally
            {
                try { await db.WebsiteCheckouts.Where(c => c.Id == checkout.Id && c.CompletedOrderId == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.ReconciliationCheckedAtUtc, DateTimeOffset.UtcNow), ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { logger.LogError(ex, "Could not record Stripe reconciliation check {CheckoutId}", checkout.Id); }
            }
        }
    }

    private static Task RecordAlert(AniseDbContext db, Guid id, string message, CancellationToken ct) =>
        db.WebsiteCheckouts.Where(c => c.Id == id && c.CompletedOrderId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ReconciliationError, message)
                .SetProperty(c => c.ReconciliationCheckedAtUtc, DateTimeOffset.UtcNow), ct);

    private async Task<JsonDocument?> GetStripeJson(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.stripe.com/v1/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Stripe:SecretKey"]);
        request.Headers.Add("Stripe-Version", "2024-06-20");
        using var response = await clients.CreateClient().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Stripe reconciliation lookup failed: " + (int)response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<JsonDocument?> FindSession(Guid checkoutId, DateTimeOffset created, CancellationToken ct)
    {
        string? after = null;
        for (var page = 0; page < 20; page++)
        {
            var path = "checkout/sessions?limit=100&created%5Bgte%5D=" + created.AddMinutes(-5).ToUnixTimeSeconds();
            if (after is not null) path += "&starting_after=" + Uri.EscapeDataString(after);
            using var listing = await GetStripeJson(path, ct);
            if (listing is null) return null;
            var rows = listing.RootElement.GetProperty("data");
            foreach (var row in rows.EnumerateArray())
                if (row.TryGetProperty("client_reference_id", out var reference) && reference.GetString() == checkoutId.ToString("D"))
                    return JsonDocument.Parse(row.GetRawText());
            if (rows.GetArrayLength() == 0 || !listing.RootElement.GetProperty("has_more").GetBoolean()) return null;
            after = rows[rows.GetArrayLength() - 1].GetProperty("id").GetString();
        }
        logger.LogError("Stripe session search exceeded page limit for checkout {CheckoutId}; staff review required", checkoutId);
        return null;
    }

    public static bool TryPaidSession(JsonElement session, Guid checkoutId, WebsiteStripeMode mode, out WebsiteStripeWebhook.PaidSession? paid)
    {
        paid = null;
        if (!session.TryGetProperty("id", out var id) || !mode.MatchesSession(id.GetString()) ||
            !session.TryGetProperty("livemode", out var live) || live.ValueKind != (mode.Live ? JsonValueKind.True : JsonValueKind.False) ||
            !session.TryGetProperty("payment_status", out var status) || status.GetString() != "paid" ||
            !session.TryGetProperty("currency", out var currency) || currency.GetString() != "gbp" ||
            !session.TryGetProperty("amount_total", out var amount) || !amount.TryGetInt64(out var pence) || pence < 30 ||
            !session.TryGetProperty("client_reference_id", out var reference) || reference.GetString() != checkoutId.ToString("D")) return false;
        paid = new WebsiteStripeWebhook.PaidSession(id.GetString()!, pence, "gbp", checkoutId);
        return true;
    }
}

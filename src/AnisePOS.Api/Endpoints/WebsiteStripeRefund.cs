using System.Net.Http.Headers;
using System.Text.Json;

namespace AnisePOS.Api.Endpoints;

public static class WebsiteStripeRefund
{
    public enum Result { Complete, Pending, Unconfirmed }

    // Reconcile first: a previous request may have succeeded even if its response was lost.
    public static async Task<Result> EnsureRefundedAsync(HttpClient client, string key, string sessionId, Guid paymentId, decimal amount, CancellationToken ct)
    {
        if (!key.StartsWith("sk_test_", StringComparison.Ordinal) || !sessionId.StartsWith("cs_test_", StringComparison.Ordinal) || amount <= 0 || amount * 100m != decimal.Truncate(amount * 100m)) return Result.Unconfirmed;
        try
        {
            async Task<JsonDocument?> Send(HttpMethod method, string path, HttpContent? content = null)
            {
                using var request = new HttpRequestMessage(method, "https://api.stripe.com/v1/" + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.Add("Stripe-Version", "2024-06-20");
                if (method == HttpMethod.Post) request.Headers.Add("Idempotency-Key", "website-decline-refund-" + paymentId.ToString("N"));
                request.Content = content;
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return null;
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            }

            using var session = await Send(HttpMethod.Get, "checkout/sessions/" + Uri.EscapeDataString(sessionId) + "?expand%5B%5D=payment_intent.latest_charge");
            if (session is null) return Result.Unconfirmed;
            var root = session.RootElement;
            if (!TryCharge(root, sessionId, amount, out var chargeId, out var refunded)) return Result.Unconfirmed;
            using var refunds = await Send(HttpMethod.Get, "refunds?limit=100&charge=" + Uri.EscapeDataString(chargeId));
            if (refunds is null) return Result.Unconfirmed;
            if (OrderQueueEndpoints.HasFullRefund(root, sessionId, amount) && OrderQueueEndpoints.HasSuccessfulRefunds(refunds.RootElement, decimal.ToInt64(amount * 100m))) return Result.Complete;
            if (!refunds.RootElement.TryGetProperty("has_more", out var more) || more.ValueKind != JsonValueKind.False ||
                !refunds.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return Result.Unconfirmed;
            // Do not create a second refund if a manual or prior refund is in flight or partial.
            if (refunded != 0 || data.GetArrayLength() != 0) return Result.Pending;
            using var created = await Send(HttpMethod.Post, "refunds", new FormUrlEncodedContent(new Dictionary<string, string> { ["charge"] = chargeId, ["amount"] = decimal.ToInt64(amount * 100m).ToString(System.Globalization.CultureInfo.InvariantCulture) }));
            if (created is null) return Result.Unconfirmed;
            // Only a fresh Stripe read, including completed refund records, can finalize the order.
            using var updated = await Send(HttpMethod.Get, "checkout/sessions/" + Uri.EscapeDataString(sessionId) + "?expand%5B%5D=payment_intent.latest_charge");
            using var updatedRefunds = await Send(HttpMethod.Get, "refunds?limit=100&charge=" + Uri.EscapeDataString(chargeId));
            return updated is not null && updatedRefunds is not null && OrderQueueEndpoints.HasFullRefund(updated.RootElement, sessionId, amount) && OrderQueueEndpoints.HasSuccessfulRefunds(updatedRefunds.RootElement, decimal.ToInt64(amount * 100m)) ? Result.Complete : Result.Pending;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or OverflowException)
        { return Result.Unconfirmed; }
    }

    private static bool TryCharge(JsonElement root, string sessionId, decimal expected, out string chargeId, out long refunded)
    {
        chargeId = ""; refunded = 0;
        if (!root.TryGetProperty("id", out var id) || id.GetString() != sessionId ||
            !root.TryGetProperty("livemode", out var live) || live.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("payment_status", out var status) || status.GetString() != "paid" ||
            !root.TryGetProperty("currency", out var currency) || currency.GetString() != "gbp" ||
            !root.TryGetProperty("amount_total", out var total) || !total.TryGetInt64(out var pence) || pence != expected * 100m ||
            !root.TryGetProperty("payment_intent", out var intent) || intent.ValueKind != JsonValueKind.Object ||
            !intent.TryGetProperty("latest_charge", out var charge) || charge.ValueKind != JsonValueKind.Object ||
            !charge.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idValue.GetString()) ||
            !charge.TryGetProperty("currency", out var chargeCurrency) || chargeCurrency.GetString() != "gbp" ||
            !charge.TryGetProperty("amount", out var charged) || !charged.TryGetInt64(out var chargedAmount) || chargedAmount != pence ||
            !charge.TryGetProperty("amount_refunded", out var refundedValue) || !refundedValue.TryGetInt64(out refunded) || refunded < 0 || refunded > pence) return false;
        chargeId = idValue.GetString()!;
        return true;
    }
}

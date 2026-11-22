using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using AnisePOS.Api.Data;
using AnisePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Endpoints;

// Card data must never pass through this application.
public static class StripePaymentEndpoints
{
    public static bool IsConfirmedPayment(string? status, string? currency, long amountReceived, decimal expectedAmount) =>
        status == "succeeded" && currency == "gbp" && expectedAmount > 0 &&
        expectedAmount * 100 == amountReceived;

    // A declined-looking intent can still be subject to an in-flight confirmation.
    // Only a terminal canceled intent can safely precede a new payment attempt.
    public static bool CanStartNewAttempt(string? stripeStatus, bool isLiveMode) =>
        !isLiveMode && stripeStatus == "canceled";

    public static void MapStripePaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var payments = app.MapGroup("/api/stripe-payments").RequireAuthorization();
        payments.MapGet("/config", (IConfiguration configuration, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var key = configuration["Stripe:PublishableKey"];
            return !string.IsNullOrWhiteSpace(key) && key.StartsWith("pk_", StringComparison.Ordinal)
                ? Results.Ok(new { publishableKey = key })
                : Results.Problem("Stripe card entry is not configured.", statusCode: 503);
        });
        // This readiness check never returns either credential and only accepts sandbox keys.
        payments.MapGet("/sandbox-ready", async (IConfiguration configuration, IHttpClientFactory clients, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var secret = configuration["Stripe:SecretKey"];
            var publishable = configuration["Stripe:PublishableKey"];
            if (string.IsNullOrWhiteSpace(secret) || !secret.StartsWith("sk_test_", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(publishable) || !publishable.StartsWith("pk_test_", StringComparison.Ordinal))
                return Results.Problem("Configure Stripe test publishable and secret keys on the server.", statusCode: 503);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.stripe.com/v1/account");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                using var response = await clients.CreateClient().SendAsync(request, ct);
                return response.IsSuccessStatusCode
                    ? Results.Ok(new { ready = true })
                    : Results.Problem("Stripe could not verify the test secret key.", statusCode: 502);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Problem("Stripe is unavailable; try again later.", statusCode: 502);
            }
        });
        // Create an unconfirmed intent. The deterministic idempotency key lets a later
        // request recover the same intent if the Stripe response or database save fails.
        payments.MapPost("/{orderNumber:long}/prepare", async (long orderNumber, AniseDbContext db,
            IConfiguration configuration, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var secret = configuration["Stripe:SecretKey"];
            var publishable = configuration["Stripe:PublishableKey"];
            if (string.IsNullOrWhiteSpace(secret) || !secret.StartsWith("sk_test_", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(publishable) || !publishable.StartsWith("pk_test_", StringComparison.Ordinal))
                return Results.Problem("Stripe sandbox keys are not configured.", statusCode: 503);
            var payment = await db.Payments.Include(p => p.Order).Where(p => p.Order.OrderNumber == orderNumber)
                .OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct);
            if (payment is null) return Results.NotFound();
            if (payment.Order.OrderType != OrderType.Delivery || payment.Method != PaymentMethod.Card ||
                payment.Status != PaymentStatus.Pending || payment.Amount != payment.Order.Total ||
                payment.Amount < 0.30m || payment.Amount * 100 != decimal.Truncate(payment.Amount * 100))
                return Results.Conflict(new { message = "This order is not eligible for a Stripe payment." });
            if (payment.Provider is not null and not "Stripe")
                return Results.Conflict(new { message = "Another payment provider is already recorded." });
            if (!string.IsNullOrEmpty(payment.ProviderReference))
                return Results.Ok(new { prepared = true });
            // Claim the payment before contacting Stripe, preventing manual payment marking.
            var claimed = await db.Payments.Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Pending &&
                    (p.Provider == null || p.Provider == "Stripe"))
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.Provider, "Stripe"), ct);
            if (claimed != 1) return Results.Conflict(new { message = "Payment status changed. Refresh the order." });
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/payment_intents");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                request.Headers.Add("Idempotency-Key", "anise-intent-" + payment.Id.ToString("N"));
                request.Headers.Add("Stripe-Version", "2024-06-20");
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                    ["amount"] = decimal.Truncate(payment.Amount * 100).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["currency"] = "gbp", ["payment_method_types[0]"] = "card",
                    ["metadata[order_number]"] = orderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
                using var response = await clients.CreateClient().SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    return Results.Problem("Could not prepare the Stripe payment. Retry preparation for this same order.", statusCode: 502);
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var root = document.RootElement;
                var intentId = root.GetProperty("id").GetString();
                if (root.GetProperty("livemode").GetBoolean() || intentId is null || !intentId.StartsWith("pi_", StringComparison.Ordinal))
                    return Results.Problem("Unexpected Stripe response; payment was not confirmed.", statusCode: 502);
                await db.Payments.Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Pending && p.Provider == "Stripe" && p.ProviderReference == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ProviderReference, intentId), ct);
                return Results.Ok(new { prepared = true });
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException)
            {
                return Results.Problem("Payment preparation could not be confirmed. Retry preparation for this same order.", statusCode: 502);
            }
        });
        // Claim the confirmation exactly once. A lost response is resolved through
        // reconciliation; confirmation is never automatically attempted a second time.
        payments.MapPost("/{orderNumber:long}/confirm", async (long orderNumber, StripeConfirmRequest body,
            AniseDbContext db, IConfiguration configuration, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var secret = configuration["Stripe:SecretKey"];
            var publishable = configuration["Stripe:PublishableKey"];
            if (secret is null || !secret.StartsWith("sk_test_", StringComparison.Ordinal) ||
                publishable is null || !publishable.StartsWith("pk_test_", StringComparison.Ordinal))
                return Results.Problem("Stripe sandbox keys are not configured.", statusCode: 503);
            if (string.IsNullOrWhiteSpace(body.PaymentMethodId) || !body.PaymentMethodId.StartsWith("pm_", StringComparison.Ordinal) ||
                body.PaymentMethodId.Length > 150 || body.PaymentMethodId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                return Results.BadRequest(new { message = "Invalid payment method." });
            var payment = await db.Payments.AsNoTracking().Include(p => p.Order)
                .Where(p => p.Order.OrderNumber == orderNumber).OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct);
            if (payment is null) return Results.NotFound();
            if (payment.Order.OrderType != OrderType.Delivery || payment.Method != PaymentMethod.Card ||
                payment.Amount != payment.Order.Total || payment.Provider != "Stripe" || payment.Status != PaymentStatus.Pending ||
                string.IsNullOrEmpty(payment.ProviderReference) || !payment.ProviderReference.StartsWith("pi_", StringComparison.Ordinal) ||
                payment.ProviderReference.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                return Results.Conflict(new { message = "Payment cannot be confirmed. Check the order payment status." });
            var claimed = await db.Payments.Where(p => p.Id == payment.Id && p.Provider == "Stripe" &&
                    p.Status == PaymentStatus.Pending && p.ProviderReference == payment.ProviderReference)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Provider, "StripeConfirming"), ct);
            if (claimed != 1) return Results.Conflict(new { message = "Payment is already being processed. Check its status." });
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.stripe.com/v1/payment_intents/" + payment.ProviderReference + "/confirm");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                request.Headers.Add("Stripe-Version", "2024-06-20");
                request.Headers.Add("Idempotency-Key", "anise-confirm-" + payment.Id.ToString("N"));
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                    ["payment_method"] = body.PaymentMethodId,
                    ["payment_method_options[card][moto]"] = "true"
                });
                using var response = await clients.CreateClient().SendAsync(request, ct);
                // Even a decline is reconciled separately: do not infer a final status
                // from a transport failure or allow another confirmation here.
                if (response.IsSuccessStatusCode)
                    return Results.Ok(new { message = "Confirmation submitted. Check the payment status." });
                // Only expose Stripe's machine-readable error code, never card details,
                // payment method identifiers, raw responses, or request credentials.
                string? code = null;
                try
                {
                    using var errorDocument = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (errorDocument.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var errorCode))
                        code = errorCode.GetString();
                }
                catch (JsonException) { }
                if (code is null || code.Length > 80 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) code = "unknown";
                return Results.Json(new { message = "Stripe did not confirm the payment. Check its status before taking further action.", stripeErrorCode = code }, statusCode: 502);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Problem("Payment result is unknown. Check the payment status before taking further action.", statusCode: 502);
            }
        });
        // Cancel the existing intent before any replacement can be considered.
        // Stripe decides atomically whether cancellation is still possible.
        payments.MapPost("/{orderNumber:long}/cancel-for-retry", async (long orderNumber, AniseDbContext db,
            IConfiguration configuration, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var secret = configuration["Stripe:SecretKey"];
            var publishable = configuration["Stripe:PublishableKey"];
            if (secret is null || !secret.StartsWith("sk_test_", StringComparison.Ordinal) ||
                publishable is null || !publishable.StartsWith("pk_test_", StringComparison.Ordinal))
                return Results.Problem("Stripe sandbox keys are not configured.", statusCode: 503);
            var payment = await db.Payments.AsNoTracking().Include(p => p.Order)
                .Where(p => p.Order.OrderNumber == orderNumber).OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct);
            if (payment is null) return Results.NotFound();
            if (payment.Order.OrderType != OrderType.Delivery || payment.Method != PaymentMethod.Card ||
                payment.Status != PaymentStatus.Pending || payment.Provider is not ("Stripe" or "StripeConfirming") ||
                string.IsNullOrWhiteSpace(payment.ProviderReference) || !payment.ProviderReference.StartsWith("pi_", StringComparison.Ordinal) ||
                payment.ProviderReference.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                return Results.Conflict(new { message = "This payment cannot be cancelled for retry. Check its status." });
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.stripe.com/v1/payment_intents/" + payment.ProviderReference + "/cancel");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                request.Headers.Add("Stripe-Version", "2024-06-20");
                request.Headers.Add("Idempotency-Key", "anise-cancel-" + payment.Id.ToString("N"));
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>());
                using var response = await clients.CreateClient().SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    return Results.Problem("Stripe did not confirm cancellation. Check payment status before retrying.", statusCode: 502);
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var result = document.RootElement;
                if (result.GetProperty("id").GetString() != payment.ProviderReference ||
                    !CanStartNewAttempt(result.GetProperty("status").GetString(), result.GetProperty("livemode").GetBoolean()))
                    return Results.Problem("Stripe did not confirm cancellation. Check payment status before retrying.", statusCode: 502);
                var updated = await db.Payments.Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Pending &&
                    (p.Provider == "Stripe" || p.Provider == "StripeConfirming") && p.ProviderReference == payment.ProviderReference)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Provider, "StripeCancelled"), ct);
                return updated == 1 ? Results.Ok(new { status = "Cancelled", retryAvailable = false })
                    : Results.Conflict(new { message = "The order changed. Refresh and check its payment status." });
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException)
            {
                return Results.Problem("Cancellation result is unknown. Check Stripe before retrying.", statusCode: 502);
            }
        });
        // A replacement has its own payment ID and idempotency keys. Serializable
        // isolation prevents concurrent requests from creating two replacements.
        payments.MapPost("/{orderNumber:long}/replacement", async (long orderNumber, AniseDbContext db,
            IConfiguration configuration, ClaimsPrincipal staff, CancellationToken ct) =>
        {
            var secret = configuration["Stripe:SecretKey"];
            var publishable = configuration["Stripe:PublishableKey"];
            if (secret is null || !secret.StartsWith("sk_test_", StringComparison.Ordinal) ||
                publishable is null || !publishable.StartsWith("pk_test_", StringComparison.Ordinal))
                return Results.Problem("Stripe sandbox keys are not configured.", statusCode: 503);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            var order = await db.Orders.Include(o => o.Payments).SingleOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);
            if (order is null) return Results.NotFound();
            var previous = order.Payments.OrderByDescending(p => p.CreatedAtUtc).ThenByDescending(p => p.Id).FirstOrDefault();
            if (order.OrderType != OrderType.Delivery || order.Status is OrderStatus.Cancelled or OrderStatus.Refunded ||
                previous is null || previous.Provider != "StripeCancelled" || previous.Status != PaymentStatus.Pending ||
                previous.Method != PaymentMethod.Card || previous.Amount != order.Total ||
                order.Payments.Any(p => p.Status == PaymentStatus.Paid))
                return Results.Conflict(new { message = "A replacement cannot be created. Check the order payment status." });
            var now = DateTimeOffset.UtcNow;
            previous.Status = PaymentStatus.Voided;
            previous.UpdatedAtUtc = now;
            var replacement = new Payment { OrderId = order.Id, Method = PaymentMethod.Card, Status = PaymentStatus.Pending,
                Amount = order.Total, Provider = "Stripe", CreatedAtUtc = now };
            db.Payments.Add(replacement);
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = order.Id, Status = order.Status, CreatedAtUtc = now,
                ChangedBy = staff.FindFirstValue(ClaimTypes.NameIdentifier), Note = "Cancelled Stripe payment replaced for sandbox retry" });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new { status = "Pending", message = "Replacement created; card details and payment confirmation are still required." });
        });
        payments.MapPost("/{orderNumber:long}/reconcile", async (long orderNumber, AniseDbContext db,
            IConfiguration configuration, IHttpClientFactory clients, ClaimsPrincipal staff, ILoggerFactory logs, CancellationToken ct) =>
        {
            var payment = await db.Payments.Include(p => p.Order)
                .Where(p => p.Order.OrderNumber == orderNumber && (p.Provider == "Stripe" || p.Provider == "StripeConfirming"))
                .OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct);
            if (payment is null || string.IsNullOrWhiteSpace(payment.ProviderReference))
                return Results.NotFound(new { message = "No Stripe payment is recorded for this order." });
            if (payment.Order.OrderType != OrderType.Delivery || payment.Method is not (PaymentMethod.Card or PaymentMethod.Stripe))
                return Results.Conflict(new { message = "This order is not a delivery card payment." });
            if (payment.Status == PaymentStatus.Paid) return Results.Ok(new { status = "Paid" });
            if (payment.Status != PaymentStatus.Pending)
                return Results.Conflict(new { message = "This payment cannot be reconciled automatically." });
            var key = configuration["Stripe:SecretKey"];
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("sk_test_", StringComparison.Ordinal))
                return Results.Problem("Stripe sandbox is not configured.", statusCode: 503);
            if (!payment.ProviderReference.StartsWith("pi_", StringComparison.Ordinal) ||
                payment.ProviderReference.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                return Results.Conflict(new { message = "Invalid Stripe payment reference." });
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.stripe.com/v1/payment_intents/" + payment.ProviderReference);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.Add("Stripe-Version", "2024-06-20");
                using var response = await clients.CreateClient().SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    return Results.Problem("Could not verify the payment with Stripe. Check its status before retrying.", statusCode: 502);
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var root = document.RootElement;
                var status = root.GetProperty("status").GetString();
                var currency = root.GetProperty("currency").GetString();
                var received = root.GetProperty("amount_received").GetInt64();
                if (!IsConfirmedPayment(status, currency, received, payment.Amount))
                    return Results.Ok(new { status = "Pending", stripeStatus = status });
                var now = DateTimeOffset.UtcNow;
                payment.Status = PaymentStatus.Paid;
                payment.Method = PaymentMethod.Stripe;
                payment.PaidAtUtc = now;
                payment.UpdatedAtUtc = now;
                db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = payment.OrderId, Status = payment.Order.Status,
                    CreatedAtUtc = now, ChangedBy = staff.FindFirstValue(ClaimTypes.NameIdentifier), Note = "Stripe payment confirmed" });
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { status = "Paid" });
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                logs.CreateLogger("StripePayments").LogWarning("Stripe payment reconciliation failed for order {OrderNumber}", orderNumber);
                return Results.Problem("Could not verify the payment with Stripe. Check its status before retrying.", statusCode: 502);
            }
        });
    }
}

public sealed record StripeConfirmRequest(string PaymentMethodId);

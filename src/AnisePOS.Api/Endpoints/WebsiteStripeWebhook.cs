using AnisePOS.Api.Data;
using AnisePOS.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text;
using System.Text.Json;

namespace AnisePOS.Api.Endpoints;

public static class WebsiteStripeWebhook
{
    private static readonly JsonSerializerOptions SnapshotOptions = new() { PropertyNameCaseInsensitive = true };
    public sealed record PaidSession(string SessionId, long AmountPence, string Currency, Guid CheckoutId);

    // Only paid Checkout Sessions from the configured environment reach the kitchen.
    public static PaidSession? ReadPaidSession(JsonElement root, bool liveMode = false)
    {
        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            type.GetString() is not ("checkout.session.completed" or "checkout.session.async_payment_succeeded") ||
            !root.TryGetProperty("livemode", out var live) || live.ValueKind != (liveMode ? JsonValueKind.True : JsonValueKind.False) ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("object", out var session) || session.ValueKind != JsonValueKind.Object ||
            !session.TryGetProperty("object", out var objectType) || objectType.ValueKind != JsonValueKind.String || objectType.GetString() != "checkout.session" ||
            !session.TryGetProperty("livemode", out var sessionLive) || sessionLive.ValueKind != (liveMode ? JsonValueKind.True : JsonValueKind.False) ||
            !session.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not string sessionId || !sessionId.StartsWith(liveMode ? "cs_live_" : "cs_test_", StringComparison.Ordinal) ||
            !session.TryGetProperty("payment_status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "paid" ||
            !session.TryGetProperty("currency", out var currency) || currency.ValueKind != JsonValueKind.String || currency.GetString() != "gbp" ||
            !session.TryGetProperty("amount_total", out var amount) || amount.ValueKind != JsonValueKind.Number || !amount.TryGetInt64(out var pence) || pence < 30 ||
            !session.TryGetProperty("client_reference_id", out var reference) || reference.ValueKind != JsonValueKind.String || !Guid.TryParse(reference.GetString(), out var checkoutId))
            return null;
        return new PaidSession(sessionId, pence, "gbp", checkoutId);
    }

    public static void MapWebsiteStripeWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/webhooks/stripe/website", async (HttpRequest request, IConfiguration config, IHostEnvironment environment,
            AniseDbContext db, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var secret = config["WebsiteCheckout:StripeWebhookSecret"];
            var stripeMode = WebsiteStripeMode.From(config, environment);
            if (stripeMode is null || string.IsNullOrWhiteSpace(secret)) return Results.Problem("Webhook is not configured.", statusCode: 503);
            using var reader = new StreamReader(request.Body, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            string payload;
            try { payload = await reader.ReadToEndAsync(ct); }
            catch (DecoderFallbackException) { return Results.BadRequest(); }
            if (!WebsiteCheckoutValidation.VerifyStripeSignature(payload, request.Headers["Stripe-Signature"].ToString(), secret, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)))
                return Results.BadRequest();
            JsonDocument json;
            try { json = JsonDocument.Parse(payload); }
            catch (JsonException) { return Results.BadRequest(); }
            using (json)
            {
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                    return Results.BadRequest();
                if (type.GetString() is not ("checkout.session.completed" or "checkout.session.async_payment_succeeded")) return Results.Ok();
                var paid = ReadPaidSession(root, stripeMode.Live);
                if (paid is null)
                {
                    // An unpaid completed session is not fulfilled; a later paid event may follow.
                    if (root.TryGetProperty("livemode", out var live) && live.ValueKind == (stripeMode.Live ? JsonValueKind.True : JsonValueKind.False) &&
                        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                        data.TryGetProperty("object", out var obj) && obj.ValueKind == JsonValueKind.Object &&
                        obj.TryGetProperty("payment_status", out var status) && status.ValueKind == JsonValueKind.String &&
                        status.GetString() == "unpaid") return Results.Ok();
                    return Results.BadRequest();
                }
                return await FulfilPaidSessionAsync(paid, db, loggerFactory.CreateLogger("WebsiteStripeWebhook"), ct);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(256 * 1024));
    }


    public static async Task<IResult> FulfilPaidSessionAsync(PaidSession paid, AniseDbContext db, ILogger logger, CancellationToken ct)
    {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var checkout = await db.WebsiteCheckouts.SingleOrDefaultAsync(c => c.Id == paid.CheckoutId, ct);
                if (checkout is null || checkout.StripeSessionId != paid.SessionId || checkout.Total * 100m != paid.AmountPence)
                {
                    logger.LogWarning("Stripe checkout did not match a saved website checkout: {CheckoutId}", paid.CheckoutId);
                    return Results.Conflict();
                }
                if (checkout.CompletedOrderId is not null) return Results.Ok();
                CheckoutSnapshot? snapshot;
                try { snapshot = JsonSerializer.Deserialize<CheckoutSnapshot>(checkout.SnapshotJson, SnapshotOptions); }
                catch (JsonException) { return Results.Problem("Saved checkout could not be processed.", statusCode: 500); }
                if (snapshot?.Lines is null || snapshot.Lines.Count == 0 || snapshot.Total != checkout.Total ||
                    snapshot.OrderType is not (OrderType.Collection or OrderType.Delivery) ||
                    snapshot.Lines.Any(l => l.Quantity < 1 || l.Modifiers is null) ||
                    snapshot.Lines.Sum(l => l.LineTotal) != snapshot.Subtotal || snapshot.DiscountTotal < 0 || snapshot.DiscountTotal > snapshot.Subtotal + snapshot.DeliveryFee || snapshot.Subtotal + snapshot.DeliveryFee - snapshot.DiscountTotal != checkout.Total)
                    return Results.Problem("Saved checkout could not be processed.", statusCode: 500);
                var order = new Order { OrderType = snapshot.OrderType, Status = OrderStatus.Placed,
                    CustomerName = snapshot.CustomerName, CustomerPhone = snapshot.CustomerPhone,
                    DeliveryAddress = snapshot.DeliveryAddress, Notes = snapshot.Notes, Subtotal = snapshot.Subtotal,
                    DeliveryFee = snapshot.DeliveryFee, DiscountTotal = snapshot.DiscountTotal, Total = snapshot.Total };
                foreach (var line in snapshot.Lines)
                {
                    order.Lines.Add(new OrderLine { ProductId = line.ProductId, ProductVariantId = line.ProductVariantId,
                        ProductName = line.ProductName, VariantName = line.VariantName, UnitPrice = line.UnitPrice,
                        Quantity = line.Quantity, LineTotal = line.LineTotal, Notes = line.Notes,
                        Modifiers = line.Modifiers.Select(m => new OrderLineModifier { ModifierId = m.ModifierId,
                            ModifierName = m.ModifierName, PriceAdjustment = m.PriceAdjustment }).ToList() });
                }
                var now = DateTimeOffset.UtcNow;
                order.Payments.Add(new Payment { Method = PaymentMethod.Stripe, Status = PaymentStatus.Paid,
                    Amount = checkout.Total, Provider = "StripeCheckout", ProviderReference = paid.SessionId, PaidAtUtc = now });
                order.StatusHistory.Add(new OrderStatusHistory { Status = OrderStatus.Placed, Note = "Website card payment confirmed by Stripe" });
                db.Orders.Add(order);
                if (snapshot.RecordPromoRedemption && !string.IsNullOrWhiteSpace(snapshot.PromoCode) && !string.IsNullOrWhiteSpace(snapshot.CustomerPhone))
                    db.PromoRedemptions.Add(new PromoRedemption { Code = snapshot.PromoCode, Phone = PromoCodes.NormalizePhone(snapshot.CustomerPhone), OrderId = order.Id });
                if (!string.IsNullOrEmpty(snapshot.ProtectedConfirmation))
                    db.WebsiteConfirmations.Add(new WebsiteConfirmation { Order = order, ProtectedPayload = snapshot.ProtectedConfirmation });
                checkout.CompletedOrderId = order.Id; checkout.ReconciliationError = null; checkout.UpdatedAtUtc = now;
                if (!await OrderNumbers.SaveNewOrderAsync(db, order, ct)) return Results.Problem("Could not allocate an order number. Please retry.", statusCode: 503);
                await transaction.CommitAsync(ct);
                return Results.Ok();
    }

    private sealed record CheckoutSnapshot(OrderType OrderType, string? CustomerName, string? CustomerPhone,
        string? DeliveryAddress, decimal Subtotal, decimal DeliveryFee, decimal Total, List<SnapshotLine> Lines, string? Notes = null, string? ProtectedConfirmation = null, decimal DiscountTotal = 0, string? PromoCode = null, bool RecordPromoRedemption = false);
    private sealed record SnapshotLine(Guid? ProductId, Guid? ProductVariantId, string ProductName, string VariantName,
        decimal UnitPrice, int Quantity, decimal LineTotal, string? Notes, List<SnapshotModifier> Modifiers);
    private sealed record SnapshotModifier(Guid? ModifierId, string ModifierName, decimal PriceAdjustment);
}

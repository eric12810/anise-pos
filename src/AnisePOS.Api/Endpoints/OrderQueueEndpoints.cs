using System.Security.Claims;
using System.Data;
using System.Text.Json;
using AnisePOS.Api.Data;
using AnisePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Endpoints;

public static class OrderQueueEndpoints
{
    public static bool CanTransition(OrderStatus from, OrderStatus to) => (from, to) switch
    {
        (OrderStatus.Draft or OrderStatus.Placed or OrderStatus.Accepted, OrderStatus.Preparing) => true,
        (OrderStatus.Preparing, OrderStatus.Ready) => true,
        (OrderStatus.Ready, OrderStatus.Completed) => true,
        _ => false
    };

    public static bool HasFullRefund(JsonElement session, string sessionId, decimal expectedAmount, bool liveMode = false)
    {
        return session.ValueKind == JsonValueKind.Object &&
            session.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == sessionId &&
            session.TryGetProperty("livemode", out var live) && live.ValueKind == (liveMode ? JsonValueKind.True : JsonValueKind.False) &&
            session.TryGetProperty("currency", out var currency) && currency.ValueKind == JsonValueKind.String && currency.GetString() == "gbp" &&
            session.TryGetProperty("amount_total", out var total) && total.ValueKind == JsonValueKind.Number && total.TryGetInt64(out var amount) && amount == expectedAmount * 100m && amount > 0 &&
            session.TryGetProperty("payment_intent", out var intent) && intent.ValueKind == JsonValueKind.Object &&
            intent.TryGetProperty("latest_charge", out var charge) && charge.ValueKind == JsonValueKind.Object &&
            charge.TryGetProperty("currency", out var chargeCurrency) && chargeCurrency.ValueKind == JsonValueKind.String && chargeCurrency.GetString() == "gbp" &&
            charge.TryGetProperty("amount", out var charged) && charged.ValueKind == JsonValueKind.Number && charged.TryGetInt64(out var chargedAmount) && chargedAmount == amount &&
            charge.TryGetProperty("amount_refunded", out var refunded) && refunded.ValueKind == JsonValueKind.Number && refunded.TryGetInt64(out var refundedAmount) && refundedAmount == amount &&
            charge.TryGetProperty("refunded", out var fullyRefunded) && fullyRefunded.ValueKind == JsonValueKind.True;
    }

    public static bool HasSuccessfulRefunds(JsonElement refunds, long expectedAmount)
    {
        if (expectedAmount <= 0 || refunds.ValueKind != JsonValueKind.Object ||
            !refunds.TryGetProperty("has_more", out var more) || more.ValueKind != JsonValueKind.False ||
            !refunds.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return false;
        long sum = 0;
        foreach (var refund in data.EnumerateArray())
        {
            if (refund.ValueKind != JsonValueKind.Object || !refund.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String || status.GetString() != "succeeded") continue;
            if (!refund.TryGetProperty("currency", out var currency) || currency.ValueKind != JsonValueKind.String || currency.GetString() != "gbp" ||
                !refund.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number || !amount.TryGetInt64(out var value) || value <= 0 || value > expectedAmount - sum) return false;
            sum += value;
        }
        return sum == expectedAmount;
    }

    public static void MapOrderQueueEndpoints(this IEndpointRouteBuilder app)
    {
        var queue = app.MapGroup("/api/order-queue").RequireAuthorization();
        queue.MapGet("", async (AniseDbContext db, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await db.Orders.AsNoTracking()
                .Where(o => o.Status == OrderStatus.Draft || o.Status == OrderStatus.Placed ||
                    o.Status == OrderStatus.Accepted || o.Status == OrderStatus.Preparing || o.Status == OrderStatus.Ready)
                .OrderBy(o => o.CreatedAtUtc).ThenBy(o => o.OrderNumber)
                .Select(o => new QueueOrder(o.OrderNumber, o.Status, o.OrderType, o.CreatedAtUtc,
                    o.CustomerName, o.CustomerPhone, o.DeliveryAddress, o.Notes, o.EstimatedAtUtc, o.Total,
                    o.Payments.OrderByDescending(p => p.CreatedAtUtc).Select(p => (PaymentMethod?)p.Method).FirstOrDefault(),
                    o.Payments.OrderByDescending(p => p.CreatedAtUtc).Select(p => (PaymentStatus?)p.Status).FirstOrDefault(),
                    o.Payments.OrderByDescending(p => p.CreatedAtUtc).Select(p => p.Provider).FirstOrDefault(),
                    o.Payments.OrderByDescending(p => p.CreatedAtUtc).Select(p => p.ProviderReference != null).FirstOrDefault(),
                    db.WebsiteConfirmations.Where(c => c.OrderId == o.Id && c.Kind == "DeliveryStarted").Select(c => c.State).FirstOrDefault(),
                    o.Lines.OrderBy(l => l.CreatedAtUtc).ThenBy(l => l.Id).Select(l => new QueueLine(
                        l.ProductName, l.VariantName, l.Quantity, l.Notes,
                        l.Modifiers.Select(m => m.ModifierName).ToList())).ToList()))
                .ToListAsync(ct);
        });

        queue.MapPut("/{orderNumber:long}/advance", async (long orderNumber,
            AniseDbContext db, ClaimsPrincipal staff, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var order = await db.Orders.SingleOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);
            if (order is null) return Results.NotFound();
            if (order.Status == OrderStatus.Placed && await db.Payments.AnyAsync(p => p.OrderId == order.Id && (p.Provider == "WebsiteCash" || p.Provider == "StripeCheckout"), ct))
                return Results.Conflict(new { message = "Accept the website order before preparing it." });
            var previousStatus = order.Status;
            var nextStatus = previousStatus switch
            {
                OrderStatus.Draft or OrderStatus.Placed or OrderStatus.Accepted => OrderStatus.Preparing,
                OrderStatus.Preparing => OrderStatus.Ready,
                OrderStatus.Ready => OrderStatus.Completed,
                _ => (OrderStatus?)null
            };
            if (nextStatus is null) return Results.BadRequest(new { message = "This order cannot be advanced further." });
            if (nextStatus == OrderStatus.Completed && !await db.Payments.AnyAsync(p => p.OrderId == order.Id && p.Status == PaymentStatus.Paid && p.Amount >= order.Total, ct))
                return Results.Conflict(new { message = "Mark payment as paid before completing this order." });

            var now = DateTimeOffset.UtcNow;
            var updated = await db.Orders.Where(o => o.Id == order.Id && o.Status == previousStatus)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, nextStatus.Value).SetProperty(o => o.UpdatedAtUtc, now), ct);
            if (updated != 1) return Results.Conflict(new { message = "This order has changed. Refresh the queue." });
            db.OrderStatusHistory.Add(new OrderStatusHistory
            {
                OrderId = order.Id, Status = nextStatus.Value, CreatedAtUtc = now,
                ChangedBy = staff.FindFirstValue(ClaimTypes.NameIdentifier),
                Note = $"Status changed from {previousStatus} to {nextStatus.Value}"
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { orderNumber, status = nextStatus.Value });
        });

        queue.MapPut("/{orderNumber:long}/accept", async (long orderNumber, HttpRequest request,
            AniseDbContext db, ClaimsPrincipal staff, CancellationToken ct) =>
        {
            AcceptWebsiteOrder? body;
            try { body = await request.ReadFromJsonAsync<AcceptWebsiteOrder>(ct); }
            catch (JsonException) { return Results.BadRequest(new { message = "Could not read the estimated time. Please enter it again." }); }
            if (body is null) return Results.BadRequest(new { message = "Choose an estimated time." });
            var now = DateTimeOffset.UtcNow;
            if (body.EstimatedAtUtc <= now || body.EstimatedAtUtc > now.AddDays(7))
                return Results.BadRequest(new { message = "Choose a future estimated time within the next seven days." });
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var order = await db.Orders.Include(o => o.Payments).SingleOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);
            if (order is null) return Results.NotFound();
            if (order.Status != OrderStatus.Placed || !order.Payments.Any(p => p.Provider == "WebsiteCash" || (p.Provider == "StripeCheckout" && p.Status == PaymentStatus.Paid)))
                return Results.Conflict(new { message = "This website order is no longer awaiting acceptance." });
            order.Status = OrderStatus.Accepted;
            order.EstimatedAtUtc = body.EstimatedAtUtc.ToUniversalTime(); order.UpdatedAtUtc = now;
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = order.Id, Status = OrderStatus.Accepted,
                ChangedBy = staff.FindFirstValue(ClaimTypes.NameIdentifier), Note = "Website order accepted; estimated time recorded" });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Ok(new { orderNumber, order.Status, order.EstimatedAtUtc });
        });

        queue.MapPut("/{orderNumber:long}/decline", async (long orderNumber, AniseDbContext db,
            IConfiguration config, IHostEnvironment environment, IHttpClientFactory clients, ClaimsPrincipal staff, CancellationToken ct) =>
        {
            var initial = await db.Orders.AsNoTracking().Include(o => o.Payments).SingleOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);
            if (initial is null) return Results.NotFound();
            var websitePayment = initial.Payments.SingleOrDefault(p => p.Provider is "WebsiteCash" or "StripeCheckout");
            if (initial.Status != OrderStatus.Placed || websitePayment is null)
                return Results.Conflict(new { message = "This website order is no longer awaiting acceptance." });
            if (websitePayment.Provider == "WebsiteCash" && websitePayment.Status != PaymentStatus.Pending)
                return Results.Conflict(new { message = "This cash payment has already been recorded. Resolve payment before declining." });
            if (websitePayment.Provider == "StripeCheckout")
            {
                var key = config["Stripe:SecretKey"];
                var stripeMode = WebsiteStripeMode.From(config, environment);
                if (key is null || stripeMode is null || !stripeMode.MatchesSession(websitePayment.ProviderReference))
                    return Results.Problem("Stripe refunds are not configured for this environment.", statusCode: 503);
                if (websitePayment.Status == PaymentStatus.Paid)
                {
                    // Reserve the decision before contacting Stripe. Acceptance checks this status in
                    // a serializable transaction, so an order cannot be accepted during a refund.
                    var reserved = await db.Payments.Where(p => p.Id == websitePayment.Id && p.Status == PaymentStatus.Paid && p.Order.Status == OrderStatus.Placed)
                        .ExecuteUpdateAsync(p => p.SetProperty(x => x.Status, PaymentStatus.RefundPending).SetProperty(x => x.UpdatedAtUtc, DateTimeOffset.UtcNow), ct);
                    if (reserved != 1) return Results.Conflict(new { message = "This order has changed. Refresh the queue." });
                    websitePayment.Status = PaymentStatus.RefundPending;
                }
                if (websitePayment.Status != PaymentStatus.RefundPending)
                    return Results.Conflict(new { message = "This payment cannot be refunded automatically. Check Stripe." });
                var outcome = await WebsiteStripeRefund.EnsureRefundedAsync(clients.CreateClient(), key,
                    websitePayment.ProviderReference!, websitePayment.Id, websitePayment.Amount, ct, stripeMode.Live);
                if (outcome != WebsiteStripeRefund.Result.Complete)
                    return Results.Problem(outcome == WebsiteStripeRefund.Result.Pending
                        ? "The refund is still being confirmed. The order cannot be accepted; retry decline shortly."
                        : "The refund could not be confirmed. The order cannot be accepted; retry decline or check Stripe.", statusCode: 503);
            }
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var order = await db.Orders.Include(o => o.Payments).SingleAsync(o => o.Id == initial.Id, ct);
            var payment = order.Payments.SingleOrDefault(p => p.Id == websitePayment.Id);
            if (order.Status != OrderStatus.Placed || payment is null || payment.ProviderReference != websitePayment.ProviderReference ||
                payment.Status != websitePayment.Status || payment.Amount != websitePayment.Amount)
                return Results.Conflict(new { message = "This order has changed. Refresh the queue." });
            order.Status = OrderStatus.Cancelled; order.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (payment.Provider == "StripeCheckout")
            {
                var refundedAt = DateTimeOffset.UtcNow;
                payment.Status = PaymentStatus.Refunded; payment.UpdatedAtUtc = refundedAt;
                db.Refunds.Add(new Refund { OrderId = order.Id, Amount = payment.Amount, Method = payment.Method,
                    Status = RefundStatus.Completed, CompletedAtUtc = refundedAt,
                    Reason = "Website order declined", ProviderReference = payment.ProviderReference });
            }
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = order.Id, Status = OrderStatus.Cancelled,
                ChangedBy = staff.FindFirstValue(ClaimTypes.NameIdentifier), Note = payment.Provider == "StripeCheckout" ? "Website order declined after full Stripe refund" : "Website cash order declined" });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Ok(new { orderNumber, order.Status });
        });

        queue.MapPost("/{orderNumber:long}/start-delivery", async (long orderNumber, AniseDbContext db,
            ClaimsPrincipal staff, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var order = await db.Orders.SingleOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);
            if (order is null) return Results.NotFound();
            if (order.OrderType != OrderType.Delivery || order.Status != OrderStatus.Ready)
                return Results.Conflict(new { message = "Only ready delivery orders can start delivery." });
            if (!await db.Payments.AnyAsync(p => p.OrderId == order.Id && p.Status == PaymentStatus.Paid && p.Amount >= order.Total, ct))
                return Results.Conflict(new { message = "Mark payment as paid before handing over this order." });
            var existing = await db.WebsiteConfirmations.FirstOrDefaultAsync(c => c.OrderId == order.Id && c.Kind == "DeliveryStarted", ct);
            if (existing is not null) return Results.Ok(new { state = existing.State });
            // Tracking reads this marker for both current and historical delivery orders.
            db.WebsiteConfirmations.Add(new WebsiteConfirmation { OrderId = order.Id, Kind = "DeliveryStarted", State = "Recorded", ProtectedPayload = "" });
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = order.Id, Status = order.Status,
                ChangedBy = staff.FindFirstValue(ClaimTypes.NameIdentifier), Note = "Delivery started" });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return Results.Ok(new { state = "Recorded" });
        });

        queue.MapPut("/{orderNumber:long}/payment/method", async (long orderNumber, ChangePaymentMethod request, AniseDbContext db, ClaimsPrincipal staff, CancellationToken ct) =>
        {
            if (request.Method is not PaymentMethod.Cash and not PaymentMethod.Card)
                return Results.BadRequest(new { message = "Choose Cash or Card." });
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var payment = await db.Payments.Include(p => p.Order)
                .Where(p => p.Order.OrderNumber == orderNumber)
                .OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct);
            if (payment is null) return Results.NotFound(new { message = "No payment is recorded for this order." });
            if (payment.Status != PaymentStatus.Pending || payment.Order.Status is OrderStatus.Cancelled or OrderStatus.Refunded or OrderStatus.Completed)
                return Results.Conflict(new { message = "Only pending payments on active orders can be changed." });
            if (payment.Provider != "WebsiteCash" && (payment.Provider is not null || payment.ProviderReference is not null))
                return Results.Conflict(new { message = "This payment is linked to a payment provider. Resolve it before changing the method." });
            if (payment.Method != request.Method)
            {
                var previous = payment.Method;
                payment.Method = request.Method;
                payment.UpdatedAtUtc = DateTimeOffset.UtcNow;
                db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = payment.OrderId, Status = payment.Order.Status,
                    CreatedAtUtc = payment.UpdatedAtUtc.Value, ChangedBy = staff.FindFirstValue(ClaimTypes.NameIdentifier),
                    Note = $"Payment method changed from {previous} to {payment.Method}" });
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new { orderNumber, payment.Method, payment.Status });
        });

        queue.MapPut("/{orderNumber:long}/payment/paid", async (long orderNumber, AniseDbContext db, ClaimsPrincipal staff, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var payment = await db.Payments.Include(p => p.Order)
                .Where(p => p.Order.OrderNumber == orderNumber)
                .OrderByDescending(p => p.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
            if (payment is null) return Results.NotFound(new { message = "No payment is recorded for this order." });
            if (payment.Order.Status is OrderStatus.Cancelled or OrderStatus.Refunded or OrderStatus.Completed)
                return Results.Conflict(new { message = "This order can no longer be marked paid." });
            if ((payment.Provider is "Stripe" or "StripeConfirming" or "StripeCancelled" or "StripeCheckout") && payment.Status != PaymentStatus.Paid)
                return Results.Conflict(new { message = "Stripe payments must be verified with Stripe." });
            if (payment.Status == PaymentStatus.Paid)
                return Results.Ok(new { orderNumber, payment.Method, payment.Status, payment.PaidAtUtc });
            if (payment.Status is not PaymentStatus.Pending and not PaymentStatus.Authorised)
                return Results.BadRequest(new { message = "This payment cannot be marked as paid." });
            var now = DateTimeOffset.UtcNow;
            payment.Status = PaymentStatus.Paid;
            payment.PaidAtUtc = now;
            payment.UpdatedAtUtc = now;
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = payment.OrderId, Status = payment.Order.Status,
                CreatedAtUtc = now, ChangedBy = staff.FindFirstValue(ClaimTypes.NameIdentifier),
                Note = $"{payment.Method} payment marked paid" });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { orderNumber, payment.Method, payment.Status, payment.PaidAtUtc });
        });
    }
}

public sealed record ChangePaymentMethod(PaymentMethod Method);

public sealed record AcceptWebsiteOrder(DateTimeOffset EstimatedAtUtc);

public sealed record QueueLine(string ProductName, string VariantName, int Quantity, string? Notes, List<string> Modifiers);
public sealed record QueueOrder(long OrderNumber, OrderStatus Status, OrderType OrderType,
    DateTimeOffset CreatedAtUtc, string? CustomerName, string? CustomerPhone,
    string? DeliveryAddress, string? Notes, DateTimeOffset? EstimatedAtUtc, decimal Total, PaymentMethod? PaymentMethod,
    PaymentStatus? PaymentStatus, string? PaymentProvider, bool HasPaymentReference, string? DeliveryNotificationState, List<QueueLine> Lines);

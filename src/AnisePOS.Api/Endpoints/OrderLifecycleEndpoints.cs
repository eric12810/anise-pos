using System.Security.Claims;
using AnisePOS.Api.Data;
using AnisePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Endpoints;

public static class OrderLifecycleEndpoints
{
    public static void MapOrderLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/orders").RequireAuthorization();
        orders.MapPut("/{orderNumber:long}/cancel", async (long orderNumber, CancelOrderRequest request, AniseDbContext db, ClaimsPrincipal staff, CancellationToken ct) =>
        {
            var order = await db.Orders.SingleOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);
            if (order is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Reason)) return Results.BadRequest(new { message = "Choose a cancellation reason." });
            if (order.Status is OrderStatus.Completed or OrderStatus.Cancelled or OrderStatus.Refunded)
                return Results.Conflict(new { message = "This order cannot be cancelled from its current status." });
            var now = DateTimeOffset.UtcNow;
            order.Status = OrderStatus.Cancelled; order.CancelledAtUtc = now; order.CancellationReason = request.Reason.Trim(); order.UpdatedAtUtc = now;
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = order.Id, Status = order.Status, CreatedAtUtc = now, ChangedBy = staff.FindFirstValue(ClaimTypes.NameIdentifier), Note = "Order cancelled: " + order.CancellationReason });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { orderNumber, status = order.Status, cancellationReason = order.CancellationReason, paymentMayNeedRefund = await db.Payments.AnyAsync(p => p.OrderId == order.Id && (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.PartiallyRefunded), ct) });
        });

        orders.MapPut("/{orderNumber:long}/reopen", async (long orderNumber, AniseDbContext db, ClaimsPrincipal staff, CancellationToken ct) =>
        {
            var order = await db.Orders.Include(o => o.Refunds).SingleOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);
            if (order is null) return Results.NotFound();
            if (order.Status != OrderStatus.Completed) return Results.Conflict(new { message = "Only completed orders can be reopened." });
            if (order.Refunds.Any(r => r.Status == RefundStatus.Pending)) return Results.Conflict(new { message = "Wait for pending refunds to finish before reopening this order." });
            var now = DateTimeOffset.UtcNow; order.Status = OrderStatus.Accepted; order.ReopenedAtUtc = now; order.UpdatedAtUtc = now;
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = order.Id, Status = order.Status, CreatedAtUtc = now, ChangedBy = staff.FindFirstValue(ClaimTypes.NameIdentifier), Note = "Order reopened; returned to queue" });
            await db.SaveChangesAsync(ct); return Results.Ok(new { orderNumber, status = order.Status, reopenedAtUtc = now });
        });

        orders.MapPost("/{orderNumber:long}/refunds", async (long orderNumber, CreateRefundRequest request, AniseDbContext db, ClaimsPrincipal staff, CancellationToken ct) =>
        {
            var order = await db.Orders.Include(o => o.Payments).Include(o => o.Refunds).SingleOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);
            if (order is null) return Results.NotFound();
            if (order.Status != OrderStatus.Completed) return Results.Conflict(new { message = "Only completed orders can be refunded." });
            var paid = order.Payments.Where(p => p.Status is PaymentStatus.Paid or PaymentStatus.PartiallyRefunded).Sum(p => p.Amount);
            var refunded = order.Refunds.Where(r => r.Status is RefundStatus.Pending or RefundStatus.Completed).Sum(r => r.Amount);
            var remaining = paid - refunded;
            if (request.Amount <= 0 || request.Amount > remaining) return Results.BadRequest(new { message = $"Refund must be between £0.01 and {remaining:0.00}.", remainingRefundable = remaining });
            if (string.IsNullOrWhiteSpace(request.Reason)) return Results.BadRequest(new { message = "Enter a refund reason." });
            var method = order.Payments.OrderByDescending(p => p.CreatedAtUtc).Select(p => p.Method).FirstOrDefault();
            var now = DateTimeOffset.UtcNow;
            var refund = new Refund { OrderId = order.Id, Amount = request.Amount, Method = request.Method ?? method, Reason = request.Reason.Trim(), Status = RefundStatus.Pending, ProviderReference = request.ProviderReference?.Trim(), Note = request.Note?.Trim() };
            db.Refunds.Add(refund);
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = order.Id, Status = order.Status, CreatedAtUtc = now, ChangedBy = staff.FindFirstValue(ClaimTypes.NameIdentifier), Note = $"Refund recorded as pending: £{refund.Amount:0.00} ({refund.Reason})" });
            await db.SaveChangesAsync(ct); return Results.Ok(new { refund.Id, refund.Status, refund.Amount, remainingRefundable = remaining - refund.Amount, message = "Refund saved as pending. Confirm it only after the payment provider or cash return is verified." });
        });

        orders.MapPut("/{orderNumber:long}/refunds/{refundId:guid}/complete", async (long orderNumber, Guid refundId, CompleteRefundRequest request, AniseDbContext db, ClaimsPrincipal staff, CancellationToken ct) =>
        {
            var refund = await db.Refunds.Include(r => r.Order).SingleOrDefaultAsync(r => r.Id == refundId && r.Order.OrderNumber == orderNumber, ct);
            if (refund is null) return Results.NotFound(); if (refund.Status != RefundStatus.Pending) return Results.Conflict(new { message = "This refund is no longer pending." });
            var now = DateTimeOffset.UtcNow; refund.Status = RefundStatus.Completed; refund.CompletedAtUtc = now; refund.ProviderReference = request.ProviderReference?.Trim() ?? refund.ProviderReference; refund.UpdatedAtUtc = now;
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = refund.OrderId, Status = refund.Order.Status, CreatedAtUtc = now, ChangedBy = staff.FindFirstValue(ClaimTypes.NameIdentifier), Note = $"Refund completed: £{refund.Amount:0.00}" });
            await db.SaveChangesAsync(ct); return Results.Ok(new { refund.Id, refund.Status });
        });
    }
}

public sealed record CancelOrderRequest(string Reason);
public sealed record CreateRefundRequest(decimal Amount, string Reason, PaymentMethod? Method = null, string? ProviderReference = null, string? Note = null);
public sealed record CompleteRefundRequest(string? ProviderReference = null);

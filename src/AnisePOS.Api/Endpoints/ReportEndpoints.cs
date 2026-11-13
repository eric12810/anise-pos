using AnisePOS.Api.Data;
using AnisePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Endpoints;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var reports = app.MapGroup("/api/reports").RequireAuthorization("ViewReports");
        reports.MapGet("/sales", async (DateTimeOffset from, DateTimeOffset to, int timeZoneOffsetMinutes, AniseDbContext db) =>
        {
            if (to <= from || to - from > TimeSpan.FromDays(367))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = ["Choose a valid date range of up to one year."] });
            timeZoneOffsetMinutes = Math.Clamp(timeZoneOffsetMinutes, -840, 840);

            var source = await db.Orders.AsNoTracking().Where(o => o.CreatedAtUtc >= from && o.CreatedAtUtc < to)
                .Select(o => new ReportOrder(o.CreatedAtUtc, o.OrderType, o.Status, o.Total,
                    o.Payments.OrderByDescending(p => p.CreatedAtUtc)
                        .Select(p => new ReportPayment(p.Method, p.Status, p.Amount)).FirstOrDefault()))
                .ToListAsync();
            var refunds = await db.Refunds.AsNoTracking().Where(r => r.CompletedAtUtc >= from && r.CompletedAtUtc < to && r.Status == RefundStatus.Completed ||
                r.CreatedAtUtc >= from && r.CreatedAtUtc < to && r.Status == RefundStatus.Pending)
                .Select(r => new { r.Amount, r.Method, r.Status, r.CompletedAtUtc, r.CreatedAtUtc }).ToListAsync();
            var orders = source.Where(o => o.Status is not OrderStatus.Cancelled and not OrderStatus.Refunded).ToList();
            var paid = source.Where(o => o.Payment?.Status is PaymentStatus.Paid or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded).ToList();
            var grossSales = orders.Sum(o => o.Total);
            var paidSales = paid.Sum(o => o.Payment!.Amount);
            var completedRefunds = refunds.Where(r => r.Status == RefundStatus.Completed).ToList();
            var pendingRefunds = refunds.Where(r => r.Status == RefundStatus.Pending).Sum(r => r.Amount);
            var refundTotal = completedRefunds.Sum(r => r.Amount);
            var days = orders.GroupBy(o => o.CreatedAtUtc.AddMinutes(-timeZoneOffsetMinutes).Date)
                .OrderBy(g => g.Key)
                .Select(g => new DailySalesResponse(DateOnly.FromDateTime(g.Key), g.Count(), g.Sum(o => o.Total),
                    paid.Where(o => o.CreatedAtUtc.AddMinutes(-timeZoneOffsetMinutes).Date == g.Key).Sum(o => o.Payment!.Amount)))
                .ToList();

            return Results.Ok(new SalesReportResponse(from, to, orders.Count, grossSales, paidSales,
                orders.Where(o => o.Payment?.Status is not PaymentStatus.Paid and not PaymentStatus.PartiallyRefunded and not PaymentStatus.Refunded).Sum(o => o.Total), orders.Count == 0 ? 0 : grossSales / orders.Count,
                paid.Where(o => o.Payment?.Method == PaymentMethod.Cash).Sum(o => o.Payment!.Amount),
                paid.Where(o => o.Payment?.Method == PaymentMethod.Card).Sum(o => o.Payment!.Amount),
                orders.Count(o => o.OrderType == OrderType.Collection),
                orders.Count(o => o.OrderType == OrderType.Delivery),
                source.Count(o => o.Status == OrderStatus.Cancelled),
                source.Count(o => o.Status == OrderStatus.Refunded), days,
                completedRefunds.Where(r => r.Method == PaymentMethod.Cash).Sum(r => r.Amount),
                completedRefunds.Where(r => r.Method != PaymentMethod.Cash).Sum(r => r.Amount),
                refundTotal, paidSales - refundTotal, pendingRefunds));
        });
    }

    private sealed record ReportOrder(DateTimeOffset CreatedAtUtc, OrderType OrderType, OrderStatus Status,
        decimal Total, ReportPayment? Payment);
    private sealed record ReportPayment(PaymentMethod Method, PaymentStatus Status, decimal Amount);
}

public sealed record SalesReportResponse(DateTimeOffset From, DateTimeOffset To, int OrderCount,
    decimal GrossSales, decimal PaidSales, decimal PendingSales, decimal AverageOrderValue,
    decimal CashSales, decimal CardSales, int CollectionOrders, int DeliveryOrders,
    int CancelledOrders, int RefundedOrders, List<DailySalesResponse> Days,
    decimal CompletedCashRefunds, decimal CompletedCardRefunds, decimal CompletedRefunds,
    decimal NetPaidTakings, decimal PendingRefunds);
public sealed record DailySalesResponse(DateOnly Date, int OrderCount, decimal GrossSales, decimal PaidSales);

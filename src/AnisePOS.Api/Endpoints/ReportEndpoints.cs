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
                        .Select(p => new ReportPayment(p.Method, p.Status)).FirstOrDefault()))
                .ToListAsync();
            var orders = source.Where(o => o.Status is not OrderStatus.Cancelled and not OrderStatus.Refunded).ToList();
            var paid = orders.Where(o => o.Payment?.Status == PaymentStatus.Paid).ToList();
            var grossSales = orders.Sum(o => o.Total);
            var paidSales = paid.Sum(o => o.Total);
            var days = orders.GroupBy(o => o.CreatedAtUtc.AddMinutes(-timeZoneOffsetMinutes).Date)
                .OrderBy(g => g.Key)
                .Select(g => new DailySalesResponse(DateOnly.FromDateTime(g.Key), g.Count(), g.Sum(o => o.Total),
                    g.Where(o => o.Payment?.Status == PaymentStatus.Paid).Sum(o => o.Total)))
                .ToList();

            return Results.Ok(new SalesReportResponse(from, to, orders.Count, grossSales, paidSales,
                grossSales - paidSales, orders.Count == 0 ? 0 : grossSales / orders.Count,
                paid.Where(o => o.Payment?.Method == PaymentMethod.Cash).Sum(o => o.Total),
                paid.Where(o => o.Payment?.Method == PaymentMethod.Card).Sum(o => o.Total),
                orders.Count(o => o.OrderType == OrderType.Collection),
                orders.Count(o => o.OrderType == OrderType.Delivery),
                source.Count(o => o.Status == OrderStatus.Cancelled),
                source.Count(o => o.Status == OrderStatus.Refunded), days));
        });
    }

    private sealed record ReportOrder(DateTimeOffset CreatedAtUtc, OrderType OrderType, OrderStatus Status,
        decimal Total, ReportPayment? Payment);
    private sealed record ReportPayment(PaymentMethod Method, PaymentStatus Status);
}

public sealed record SalesReportResponse(DateTimeOffset From, DateTimeOffset To, int OrderCount,
    decimal GrossSales, decimal PaidSales, decimal PendingSales, decimal AverageOrderValue,
    decimal CashSales, decimal CardSales, int CollectionOrders, int DeliveryOrders,
    int CancelledOrders, int RefundedOrders, List<DailySalesResponse> Days);
public sealed record DailySalesResponse(DateOnly Date, int OrderCount, decimal GrossSales, decimal PaidSales);

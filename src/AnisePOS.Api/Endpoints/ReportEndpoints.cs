using AnisePOS.Api.Data;
using AnisePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace AnisePOS.Api.Endpoints;

public static class ReportEndpoints
{
    public static readonly Expression<Func<Payment, bool>> IsConfirmedPayment = p =>
        p.PaidAtUtc != null && (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.PartiallyRefunded ||
        p.Status == PaymentStatus.Refunded || p.Status == PaymentStatus.RefundPending);

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
                    o.Payments.Any(p => p.PaidAtUtc != null && (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.PartiallyRefunded || p.Status == PaymentStatus.Refunded || p.Status == PaymentStatus.RefundPending))))
                .ToListAsync();
            var payments = await db.Payments.AsNoTracking().Where(IsConfirmedPayment).Where(p => p.PaidAtUtc >= from && p.PaidAtUtc < to)
                .Select(p => new ReportPayment(p.PaidAtUtc!.Value, p.Method, p.Amount)).ToListAsync();
            var refunds = await db.Refunds.AsNoTracking().Where(r =>
                (r.CompletedAtUtc >= from && r.CompletedAtUtc < to && r.Status == RefundStatus.Completed) ||
                (r.CreatedAtUtc >= from && r.CreatedAtUtc < to && r.Status == RefundStatus.Pending))
                .Select(r => new ReportRefund(r.CreatedAtUtc, r.CompletedAtUtc, r.Method, r.Status, r.Amount)).ToListAsync();
            return Results.Ok(Calculate(from, to, timeZoneOffsetMinutes, source, payments, refunds));
        });
        reports.MapGet("/end-of-day", async (DateTimeOffset from, DateTimeOffset to, AniseDbContext db) =>
        {
            if (to <= from || to - from > TimeSpan.FromDays(1.1))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = ["Choose one trading day."] });
            var payments = await db.Payments.AsNoTracking().Where(IsConfirmedPayment)
                .Where(p => p.PaidAtUtc >= from && p.PaidAtUtc < to)
                .Select(p => new EndOfDayPayment(p.Method, p.Provider, p.Amount)).ToListAsync();
            var refunds = await db.Refunds.AsNoTracking()
                .Where(r => r.Status == RefundStatus.Completed && r.CompletedAtUtc >= from && r.CompletedAtUtc < to)
                .Select(r => new EndOfDayPayment(r.Method,
                    r.Order.Payments.Where(p => p.Provider == "StripeCheckout" || p.Provider == "WebsiteCash")
                        .Select(p => p.Provider).FirstOrDefault() ??
                    r.Order.Payments.Where(p => p.Method == r.Method).OrderByDescending(p => p.CreatedAtUtc)
                        .Select(p => p.Provider).FirstOrDefault(), r.Amount))
                .ToListAsync();
            return Results.Ok(CalculateEndOfDay(payments, refunds));
        });
    }

    public static SalesReportResponse Calculate(DateTimeOffset from, DateTimeOffset to, int timeZoneOffsetMinutes,
        IEnumerable<ReportOrder> orderRows, IEnumerable<ReportPayment> paymentRows, IEnumerable<ReportRefund> refundRows)
    {
            var source = orderRows.Where(o => o.CreatedAtUtc >= from && o.CreatedAtUtc < to).ToList();
            var payments = paymentRows.Where(p => p.PaidAtUtc >= from && p.PaidAtUtc < to).ToList();
            var refunds = refundRows.Where(r => (r.Status == RefundStatus.Completed && r.CompletedAtUtc >= from && r.CompletedAtUtc < to) ||
                (r.Status == RefundStatus.Pending && r.CreatedAtUtc >= from && r.CreatedAtUtc < to)).ToList();
            var orders = source.Where(o => o.Status is not OrderStatus.Cancelled and not OrderStatus.Refunded).ToList();
            var grossSales = orders.Sum(o => o.Total);
            var paidSales = payments.Sum(p => p.Amount);
            var cashSales = payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
            var cardSales = payments.Where(p => p.Method is PaymentMethod.Card or PaymentMethod.Online or PaymentMethod.SumUp or PaymentMethod.Stripe).Sum(p => p.Amount);
            var sumUpSales = payments.Where(p => p.Method == PaymentMethod.SumUp).Sum(p => p.Amount);
            var stripeSales = payments.Where(p => p.Method == PaymentMethod.Stripe).Sum(p => p.Amount);
            var otherCardSales = cardSales - sumUpSales - stripeSales;
            var completedRefunds = refunds.Where(r => r.Status == RefundStatus.Completed).ToList();
            var pendingRefunds = refunds.Where(r => r.Status == RefundStatus.Pending).Sum(r => r.Amount);
            var refundTotal = completedRefunds.Sum(r => r.Amount);
            var days = orders.Select(o => o.CreatedAtUtc.AddMinutes(-timeZoneOffsetMinutes).Date)
                .Union(payments.Select(p => p.PaidAtUtc.AddMinutes(-timeZoneOffsetMinutes).Date)).OrderBy(d => d)
                .Select(d => new DailySalesResponse(DateOnly.FromDateTime(d),
                    orders.Count(o => o.CreatedAtUtc.AddMinutes(-timeZoneOffsetMinutes).Date == d),
                    orders.Where(o => o.CreatedAtUtc.AddMinutes(-timeZoneOffsetMinutes).Date == d).Sum(o => o.Total),
                    payments.Where(p => p.PaidAtUtc.AddMinutes(-timeZoneOffsetMinutes).Date == d).Sum(p => p.Amount)))
                .ToList();

            return new SalesReportResponse(from, to, orders.Count, grossSales, paidSales,
                orders.Where(o => !o.HasConfirmedPayment).Sum(o => o.Total), orders.Count == 0 ? 0 : grossSales / orders.Count,
                cashSales, cardSales,
                orders.Count(o => o.OrderType == OrderType.Collection),
                orders.Count(o => o.OrderType == OrderType.Delivery),
                source.Count(o => o.Status == OrderStatus.Cancelled),
                source.Count(o => o.Status == OrderStatus.Refunded), days,
                completedRefunds.Where(r => r.Method == PaymentMethod.Cash).Sum(r => r.Amount),
                completedRefunds.Where(r => r.Method != PaymentMethod.Cash).Sum(r => r.Amount),
                refundTotal, cashSales + cardSales - refundTotal, pendingRefunds,
                sumUpSales, stripeSales, otherCardSales);
    }

    public sealed record ReportOrder(DateTimeOffset CreatedAtUtc, OrderType OrderType, OrderStatus Status,
        decimal Total, bool HasConfirmedPayment);
    public sealed record ReportPayment(DateTimeOffset PaidAtUtc, PaymentMethod Method, decimal Amount);
    public sealed record ReportRefund(DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc,
        PaymentMethod Method, RefundStatus Status, decimal Amount);

    public sealed record EndOfDayPayment(PaymentMethod Method, string? Provider, decimal Amount);
    public sealed record EndOfDayAmounts(decimal WebsiteCard, decimal WebsiteCash, decimal PosCash,
        decimal SumUp, decimal PosCard, decimal RecordedPosStripe);

    public static EndOfDayAmounts CalculateEndOfDay(IEnumerable<EndOfDayPayment> payments,
        IEnumerable<EndOfDayPayment> completedRefunds)
    {
        decimal Net(Func<EndOfDayPayment, bool> predicate) =>
            payments.Where(predicate).Sum(p => p.Amount) - completedRefunds.Where(predicate).Sum(r => r.Amount);
        // Provider identifies the website channel. Method alone cannot distinguish website cash
        // or website Stripe from payments taken by staff in the POS.
        return new EndOfDayAmounts(
            Net(p => p.Provider == "StripeCheckout"),
            Net(p => p.Provider == "WebsiteCash"),
            Net(p => p.Provider is not ("StripeCheckout" or "WebsiteCash") && p.Method == PaymentMethod.Cash),
            Net(p => p.Provider is not ("StripeCheckout" or "WebsiteCash") && p.Method == PaymentMethod.SumUp),
            Net(p => p.Provider is not ("StripeCheckout" or "WebsiteCash") && p.Method is PaymentMethod.Card or PaymentMethod.Online),
            Net(p => p.Provider is not ("StripeCheckout" or "WebsiteCash") && p.Method == PaymentMethod.Stripe));
    }
}

public sealed record SalesReportResponse(DateTimeOffset From, DateTimeOffset To, int OrderCount,
    decimal GrossSales, decimal PaidSales, decimal PendingSales, decimal AverageOrderValue,
    decimal CashSales, decimal CardSales, int CollectionOrders, int DeliveryOrders,
    int CancelledOrders, int RefundedOrders, List<DailySalesResponse> Days,
    decimal CompletedCashRefunds, decimal CompletedCardRefunds, decimal CompletedRefunds,
    decimal NetPaidTakings, decimal PendingRefunds,
    decimal SumUpSales = 0, decimal StripeSales = 0, decimal OtherCardSales = 0);
public sealed record DailySalesResponse(DateOnly Date, int OrderCount, decimal GrossSales, decimal PaidSales);

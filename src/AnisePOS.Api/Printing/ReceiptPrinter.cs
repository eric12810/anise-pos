using System.Globalization;
using System.Net.Sockets;
using System.Text;
using AnisePOS.Api.Domain;

namespace AnisePOS.Api.Printing;

public sealed class ReceiptPrinter(IConfiguration configuration)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task PrintAsync(Order order, bool kitchen, CancellationToken cancellationToken, string? printerHost = null)
    {
        var host = printerHost ?? configuration["ReceiptPrinter:Host"] ?? "192.168.1.234";
        var port = configuration.GetValue("ReceiptPrinter:Port", 9100);
        var data = Format(order, kitchen);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await Gate.WaitAsync(timeout.Token);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            await client.GetStream().WriteAsync(data, timeout.Token);
        }
        finally { Gate.Release(); }
    }

    public static byte[] Format(Order order, bool kitchen)
    {
        var text = new StringBuilder();
        void Line(string? value = "") { if (value != null) text.AppendLine(value); }
        string Money(decimal value) => "GBP " + value.ToString("0.00", CultureInfo.InvariantCulture);
        Line("ANISE".PadLeft(26));
        Line((kitchen ? "KITCHEN TICKET #" : "CUSTOMER RECEIPT #") + order.OrderNumber);
        Line(order.OrderType + "  " + TimeZoneInfo.ConvertTime(order.CreatedAtUtc, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));
        Line("Status: " + order.Status);
        Line(new string('-', 48));
        if (!string.IsNullOrWhiteSpace(order.CustomerName)) Line(order.CustomerName);
        if (!string.IsNullOrWhiteSpace(order.CustomerPhone)) Line(order.CustomerPhone);
        if (order.OrderType == OrderType.Delivery && !string.IsNullOrWhiteSpace(order.DeliveryAddress)) Line(order.DeliveryAddress);
        foreach (var item in order.Lines.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id))
        {
            Line($"{item.Quantity} x {item.ProductName}" + (item.VariantName == "Regular" ? "" : " - " + item.VariantName));
            foreach (var modifier in item.Modifiers.OrderBy(x => x.CreatedAtUtc))
                Line("  + " + modifier.ModifierName + (!kitchen && modifier.PriceAdjustment != 0 ? " (" + Money(modifier.PriceAdjustment) + ")" : ""));
            if (!kitchen) Line("    " + Money(item.LineTotal));
        }
        if (!string.IsNullOrWhiteSpace(order.Notes)) { Line(); Line("NOTE: " + order.Notes); }
        if (!kitchen)
        {
            Line(new string('-', 48));
            Line("Subtotal: " + Money(order.Subtotal));
            if (order.DeliveryFee != 0) Line("Delivery: " + Money(order.DeliveryFee));
            if (order.CustomFee != 0) Line((order.CustomFeeDescription ?? "Additional charge") + ": " + Money(order.CustomFee));
            if (order.DiscountTotal != 0) Line("Discount: -" + Money(order.DiscountTotal));
            Line("TOTAL: " + Money(order.Total));
            var payment = order.Payments.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault();
            Line(payment is null ? "Payment not recorded" : $"{payment.Method} - {payment.Status}");
        }
        // Replace control characters so saved order text cannot inject printer commands.
        var safe = new string(text.ToString().Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => c == '\n' || c == '\r' || (c >= ' ' && c <= '~') ? c : '?').ToArray());
        return [0x1b, 0x40, .. Encoding.ASCII.GetBytes(safe), 0x0a, 0x0a, 0x0a, 0x1d, 0x56, 0x00];
    }
}

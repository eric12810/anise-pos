using AnisePOS.Api.Domain;
using AnisePOS.Api.Data;
using AnisePOS.Api.Endpoints;
using AnisePOS.Api.Notifications;
using AnisePOS.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;
using WebPush;

// Run the production-only EF schema mapping without opening a SQL connection.
var callerNow = DateTimeOffset.UtcNow;
var callerId = Guid.NewGuid();
if (!CallerEventEndpoints.Authorized(new string('s', 40), new string('s', 40)) ||
    CallerEventEndpoints.Authorized("wrong", new string('s', 40)) ||
    CallerEventEndpoints.Authorized(null, new string('s', 40)) ||
    CallerEventEndpoints.NormalizePhone("+44 (0)7123 456789") != "07123456789" ||
    CallerEventEndpoints.NormalizePhone("0044 7123 456789") != "07123456789" ||
    CallerEventEndpoints.NormalizePhone("07123 456789") != "07123456789" ||
    CallerEventEndpoints.NormalizePhone("+33123456789") != "+33123456789" ||
    CallerEventEndpoints.NormalizePhone("Withheld") is not null ||
    CallerEventEndpoints.FindCustomer(new[] { new Customer { Phone = "07123456789" }, new Customer { Phone = "02079460123" } }, "07123456789", c => c.Phone)?.Phone != "07123456789" ||
    CallerEventEndpoints.FindCustomer(new[] { new Customer { Phone = "02079460123" } }, "07123456789", c => c.Phone) is not null ||
    !CallerEventEndpoints.Valid(new(callerId, "Withheld", callerNow, "incoming/ringing"), callerNow) ||
    CallerEventEndpoints.Valid(new(callerId, "07123456789", callerNow.AddMinutes(-3), "incoming/ringing"), callerNow))
    throw new Exception("Caller authentication, telephone or timestamp checks failed.");
Console.WriteLine("Passed caller event authentication, customer lookup, telephone and timestamp checks.");
using (var streamPayload = JsonDocument.Parse(CallerEventEndpoints.SerializeStreamNotification(
    new CallerNotification(callerId, callerNow, "07123456789", null, null))))
{
    if (streamPayload.RootElement.GetProperty("eventId").GetGuid() != callerId ||
        streamPayload.RootElement.GetProperty("callerNumber").GetString() != "07123456789")
        throw new Exception("Caller stream payload must use browser-compatible JSON names.");
}
Console.WriteLine("Passed caller stream JSON compatibility check.");

using (var model = new AniseDbContext(new DbContextOptionsBuilder<AniseDbContext>()
           .UseSqlServer("Server=localhost;Database=offline-model-check").Options))
{
    var columns = ProductionSchemaRequirements.MappedColumns(model.Model);
    if (columns.Length == 0 || columns.Any(x => x.EndsWith('.')) ||
        columns.Length != columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() ||
        !columns.Contains("dbo.Orders.Id") ||
        !columns.Contains("dbo.WebsiteCheckouts.ReconciliationError") ||
        !columns.Contains("dbo.Refunds.Id"))
        throw new Exception("Production EF schema mapping returned missing or malformed column names.");
    Console.WriteLine($"Passed production EF schema mapping for {columns.Length} columns without SQL access.");
}

var day1 = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
if (!CommerceEndpoints.ValidSavedCustomerAddress("Jane Smith", "07123456789", "NW1 8ET", "10 High Street", "Flat 2") ||
    CommerceEndpoints.ValidSavedCustomerAddress("", "07123456789", "NW1 8ET", "10 High Street", null) ||
    CommerceEndpoints.ValidSavedCustomerAddress("Jane Smith", "123", "NW1 8ET", "10 High Street", null) ||
    CommerceEndpoints.ValidSavedCustomerAddress("Jane Smith", "07123abc789", "NW1 8ET", "10 High Street", null) ||
    CommerceEndpoints.ValidSavedCustomerAddress("Jane Smith", "07123456789", "", "10 High Street", null) ||
    CommerceEndpoints.ValidSavedCustomerAddress("Jane Smith", "07123456789", "NW1 8ET", "", null) ||
    CommerceEndpoints.ValidSavedCustomerAddress(new string('A', 81), "07123456789", "NW1 8ET", "10 High Street", null))
    throw new Exception("Saved customer address validation checks failed.");
Console.WriteLine("Passed saved customer address validation checks.");
var day2 = day1.AddDays(1);
var confirmedPayment = ReportEndpoints.IsConfirmedPayment.Compile();
if (confirmedPayment(new Payment { Status = PaymentStatus.Pending, PaidAtUtc = day1, Amount = 99m }) ||
    confirmedPayment(new Payment { Status = PaymentStatus.Paid, Amount = 99m }) ||
    !confirmedPayment(new Payment { Status = PaymentStatus.Paid, PaidAtUtc = day2, Amount = 10m }) ||
    !confirmedPayment(new Payment { Status = PaymentStatus.RefundPending, PaidAtUtc = day1, Amount = 10m }) ||
    !confirmedPayment(new Payment { Status = PaymentStatus.Refunded, PaidAtUtc = day1, Amount = 10m }))
    throw new Exception("Confirmed payment eligibility checks failed.");
var reportOrders = new[]
{
    new ReportEndpoints.ReportOrder(day1.AddHours(10), OrderType.Collection, OrderStatus.Completed, 10m, true),
    new ReportEndpoints.ReportOrder(day1.AddHours(11), OrderType.Delivery, OrderStatus.Cancelled, 20m, true),
    new ReportEndpoints.ReportOrder(day1.AddHours(12), OrderType.Collection, OrderStatus.Ready, 8m, false),
    new ReportEndpoints.ReportOrder(day2.AddHours(9), OrderType.Delivery, OrderStatus.Completed, 15m, true)
};
var reportPayments = new[]
{
    new ReportEndpoints.ReportPayment(day2.AddHours(1), PaymentMethod.Cash, 10m),
    new ReportEndpoints.ReportPayment(day1.AddHours(11), PaymentMethod.Card, 20m),
    new ReportEndpoints.ReportPayment(day2.AddHours(9), PaymentMethod.Online, 15m)
};
var reportRefunds = new[]
{
    new ReportEndpoints.ReportRefund(day1.AddHours(12), day2.AddHours(2), PaymentMethod.Card, RefundStatus.Completed, 5m),
    new ReportEndpoints.ReportRefund(day2.AddHours(3), day2.AddHours(3), PaymentMethod.Card, RefundStatus.Completed, 7m),
    new ReportEndpoints.ReportRefund(day2.AddHours(4), null, PaymentMethod.Card, RefundStatus.Pending, 8m)
};
var firstReport = ReportEndpoints.Calculate(day1, day2, 0, reportOrders, reportPayments, reportRefunds);
var secondReport = ReportEndpoints.Calculate(day2, day2.AddDays(1), 0, reportOrders, reportPayments, reportRefunds);
var boundaryReport = ReportEndpoints.Calculate(day1, day2, 60, reportOrders,
    [new ReportEndpoints.ReportPayment(day1.AddMinutes(30), PaymentMethod.Cash, 4m)], []);
if (boundaryReport.Days.Any(d => d.PaidSales == 4m && d.Date != DateOnly.FromDateTime(day1.AddDays(-1).Date)))
    throw new Exception("Trading-day offset check failed.");
if (firstReport.OrderCount != 2 || firstReport.CollectionOrders != 2 || firstReport.DeliveryOrders != 0 ||
    firstReport.GrossSales != 18m || firstReport.PendingSales != 8m || firstReport.CashSales != 0m ||
    firstReport.CardSales != 20m || firstReport.CompletedRefunds != 0m || firstReport.NetPaidTakings != 20m ||
    firstReport.CancelledOrders != 1 || firstReport.Days.Single().PaidSales != 20m ||
    secondReport.OrderCount != 1 || secondReport.CashSales != 10m || secondReport.CardSales != 15m ||
    secondReport.CompletedRefunds != 12m || secondReport.PendingRefunds != 8m ||
    secondReport.NetPaidTakings != 13m || secondReport.Days.Single().PaidSales != 25m)
    throw new Exception("Payment, cancelled order, partial refund or trading-day reporting checks failed.");
Console.WriteLine("Passed payment-date, refund-date, cancelled order, partial refund and unpaid-order reporting checks.");

var corsOptions = new CorsOptions();
WebsiteCors.Configure(corsOptions);
var websiteOrigins = new[]
{
    "https://www.loveanise.co.uk",
    "https://loveanise.co.uk",
    "https://www.staging.loveanise.co.uk",
    "https://staging.loveanise.co.uk"
};
var menuCors = corsOptions.GetPolicy("PublicWebsiteMenu")!;
var checkoutCors = corsOptions.GetPolicy("PublicWebsiteCheckout")!;
if (websiteOrigins.Any(origin => !menuCors.Origins.Contains(origin)) ||
    websiteOrigins.Any(origin => !checkoutCors.Origins.Contains(origin)) ||
    menuCors.Origins.Contains("https://unsupported.example") ||
    !new HashSet<string>(menuCors.Methods).SetEquals(new[] { "GET" }) ||
    !new HashSet<string>(checkoutCors.Methods).SetEquals(new[] { "POST" }) ||
    !new HashSet<string>(checkoutCors.Headers).SetEquals(new[] { "Content-Type" }) ||
    menuCors.AllowAnyOrigin || checkoutCors.AllowAnyOrigin)
    throw new Exception("Website CORS origin, method or header policy checks failed.");
Console.WriteLine("Passed website CORS origin, method, header and unsupported-origin checks.");


if (PromoCodes.NormalizePhone("07123 456789") != PromoCodes.NormalizePhone("+44 7123 456789") ||
    PromoCodes.NormalizePhone("0044 7123 456789") != "07123456789" ||
    PromoCodes.NormalizePhone("+44 (0)7123 456789") != "07123456789" ||
    PromoCodes.NormalizePhone("020 7916 4976") != 
    PromoCodes.NormalizePhone("+44 20 7916 4976"))
    throw new Exception("Promo phone normalization failed.");
Console.WriteLine("Passed promo phone normalization checks.");

var productA = Guid.NewGuid(); var productB = Guid.NewGuid(); var category = Guid.NewGuid();
var promotionItems = new List<PromotionItem> { new(productA, new[] { category }, 10m, 2), new(productB, Array.Empty<Guid>(), 5m, 1) };
PromotionRule Rule(PromotionKind kind) => new(Guid.NewGuid(), "Offer", kind, PromotionAudience.All, PromotionFulfilment.Both, 0, 10, productA, category, 12, new[] { new BundleRequirement(productA, 1), new BundleRequirement(productB, 1) });
if (PromotionCalculator.Evaluate(Rule(PromotionKind.ItemDiscount), promotionItems, 25m, 3m, OrderType.Delivery, false)?.FoodDiscount != 2m ||
    PromotionCalculator.Evaluate(Rule(PromotionKind.CategoryDiscount), promotionItems, 25m, 3m, OrderType.Delivery, false)?.FoodDiscount != 2m ||
    PromotionCalculator.Evaluate(Rule(PromotionKind.FreeItem), promotionItems, 25m, 3m, OrderType.Delivery, false)?.FoodDiscount != 10m ||
    PromotionCalculator.Evaluate(Rule(PromotionKind.Bundle), promotionItems, 25m, 3m, OrderType.Delivery, false)?.FoodDiscount != 3m ||
    PromotionCalculator.Evaluate(Rule(PromotionKind.FreeDelivery), promotionItems, 25m, 3m, OrderType.Collection, false)?.Total != 0m ||
    PromotionCalculator.Evaluate(Rule(PromotionKind.ItemDiscount) with { Audience = PromotionAudience.Existing }, promotionItems, 25m, 3m, OrderType.Collection, false) != null)
    throw new Exception("Promotion pricing checks failed.");
var choiceRule = Rule(PromotionKind.Bundle) with { BundleItems = new[] { new BundleRequirement(productA, 1) }, ChoiceProducts = new[] { productB }, ChoiceQuantity = 1, BundlePrice = 12m };
if (PromotionCalculator.Evaluate(choiceRule, promotionItems, 25m, 3m, OrderType.Delivery, false)?.FoodDiscount != 3m ||
    PromotionCalculator.Evaluate(choiceRule with { ChoiceQuantity = 2 }, promotionItems, 25m, 3m, OrderType.Delivery, false) != null ||
    PromotionCalculator.Evaluate(Rule(PromotionKind.FreeDelivery) with { Fulfilment = PromotionFulfilment.Delivery }, promotionItems, 25m, 3m, OrderType.Collection, false) != null ||
    PromotionCalculator.Best(new[] { Rule(PromotionKind.ItemDiscount), Rule(PromotionKind.FreeItem) }, promotionItems, 3m, OrderType.Delivery, false)?.Total != 10m)
    throw new Exception("Promotion bundle or selection checks failed.");
Console.WriteLine("Passed promotion pricing checks.");

var legacyCheckout = new WebsiteOrderEndpoints.WebsiteCashOrder(Guid.NewGuid(), OrderType.Collection, "Test", "07123456789", null, null, null, new List<WebsiteOrderEndpoints.WebsiteOrderLine>(), 0m);
if (System.Text.Json.JsonSerializer.Serialize(legacyCheckout).Contains("IsReturningCustomer") ||
    !System.Text.Json.JsonSerializer.Serialize(legacyCheckout with { IsReturningCustomer = true }).Contains("IsReturningCustomer"))
    throw new Exception("Checkout reference compatibility check failed.");
Console.WriteLine("Passed checkout reference compatibility checks.");

var displayNow = DateTimeOffset.UtcNow;
var displaySettings = new PromotionSettings("Sale", null, true, false, false, PromotionKind.ItemDiscount,
    PromotionAudience.Existing, PromotionFulfilment.Delivery, 15m, 10m, productA, null, 0m, new List<BundleRequirement>());
if (!PromotionEndpoints.EligibleItemDiscount(displaySettings, displayNow, true, OrderType.Delivery) ||
    PromotionEndpoints.EligibleItemDiscount(displaySettings, displayNow, false, OrderType.Delivery) ||
    PromotionEndpoints.EligibleItemDiscount(displaySettings, displayNow, true, OrderType.Collection) ||
    PromotionEndpoints.EligibleItemDiscount(displaySettings with { EndsAtUtc = displayNow }, displayNow, true, OrderType.Delivery) ||
    PromotionEndpoints.EligibleItemDiscount(displaySettings with { Kind = PromotionKind.FreeItem }, displayNow, true, OrderType.Delivery))
    throw new Exception("Item discount visibility checks failed.");
Console.WriteLine("Passed item discount visibility checks.");

var accepted = new HashSet<(OrderStatus, OrderStatus)>
{
    (OrderStatus.Draft, OrderStatus.Preparing),
    (OrderStatus.Placed, OrderStatus.Preparing),
    (OrderStatus.Accepted, OrderStatus.Preparing),
    (OrderStatus.Preparing, OrderStatus.Ready),
    (OrderStatus.Ready, OrderStatus.Completed)
};
var statuses = Enum.GetValues<OrderStatus>().Append((OrderStatus)999).ToArray();
var checks = 0;
foreach (var from in statuses)
foreach (var to in statuses)
{
    if (OrderQueueEndpoints.CanTransition(from, to) != accepted.Contains((from, to)))
        throw new Exception($"Unexpected transition permission: {from} -> {to}");
    checks++;
}
Console.WriteLine($"Passed {checks} transition checks, including skipped stages, repeats, terminal states and invalid status values.");

if (!StripePaymentEndpoints.IsConfirmedPayment("succeeded", "gbp", 1850, 18.50m) ||
    StripePaymentEndpoints.IsConfirmedPayment("processing", "gbp", 1850, 18.50m) ||
    StripePaymentEndpoints.IsConfirmedPayment("succeeded", "usd", 1850, 18.50m) ||
    StripePaymentEndpoints.IsConfirmedPayment("succeeded", "gbp", 1800, 18.50m) ||
    StripePaymentEndpoints.IsConfirmedPayment("succeeded", "gbp", 0, 0m))
    throw new Exception("Stripe payment confirmation checks failed.");
Console.WriteLine("Passed Stripe payment confirmation checks.");

if (!StripePaymentEndpoints.CanStartNewAttempt("canceled", false) ||
    StripePaymentEndpoints.CanStartNewAttempt("canceled", true) ||
    StripePaymentEndpoints.CanStartNewAttempt("requires_payment_method", false) ||
    StripePaymentEndpoints.CanStartNewAttempt("processing", false) ||
    StripePaymentEndpoints.CanStartNewAttempt("succeeded", false) ||
    StripePaymentEndpoints.CanStartNewAttempt(null, false))
    throw new Exception("Stripe retry eligibility checks failed.");
Console.WriteLine("Passed Stripe retry eligibility checks.");

var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
var payload = "{\"id\":\"evt_test\"}";
var key = "whsec_test";
var signed = $"{now.ToUnixTimeSeconds()}.{payload}";
var digest = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
    System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(signed)));
var header = $"t={now.ToUnixTimeSeconds()},v1={digest}";
if (!WebsiteCheckoutValidation.VerifyStripeSignature(payload, header, key, now, TimeSpan.FromMinutes(5)) ||
    WebsiteCheckoutValidation.VerifyStripeSignature(payload + " ", header, key, now, TimeSpan.FromMinutes(5)) ||
    WebsiteCheckoutValidation.VerifyStripeSignature(payload, header, key, now.AddMinutes(6), TimeSpan.FromMinutes(5)) ||
    WebsiteCheckoutValidation.VerifyStripeSignature(payload, header + ",t=1", key, now, TimeSpan.FromMinutes(5)))
    throw new Exception("Webhook signature validation failed.");
if (!WebsiteCheckoutValidation.IsInDeliveryArea(51.5435, -0.1493, 51.5435, -0.1493, 2) ||
    WebsiteCheckoutValidation.IsInDeliveryArea(51.5435, -0.1493, 51.60, -0.1493, 2) ||
    WebsiteCheckoutValidation.IsInDeliveryArea(double.NaN, 0, 0, 0, 2))
    throw new Exception("Delivery area validation failed.");
Console.WriteLine("Passed website checkout signature and delivery checks.");

if (!WebsiteCheckoutValidation.ValidName("Jean O’Neil") || WebsiteCheckoutValidation.ValidName("   ") ||
    !WebsiteCheckoutValidation.ValidPhone("+44 20 7916 4976") || WebsiteCheckoutValidation.ValidPhone("123") ||
    !WebsiteCheckoutValidation.ValidAddressLine("1 Kent House", true) || WebsiteCheckoutValidation.ValidAddressLine("  ", true) ||
    !WebsiteCheckoutValidation.ValidAddressLine("", false) ||
    !WebsiteCheckoutValidation.ValidPostcode("NW1 8ET") || WebsiteCheckoutValidation.ValidPostcode("not a postcode"))
    throw new Exception("Customer details validation failed.");
Console.WriteLine("Passed customer details validation checks.");

var checkoutId = Guid.NewGuid();
var eventJson = System.Text.Json.JsonSerializer.Serialize(new { type = "checkout.session.completed", livemode = false, data = new { @object = new { @object = "checkout.session", livemode = false, id = "cs_test_123", payment_status = "paid", currency = "gbp", amount_total = 1850, client_reference_id = checkoutId.ToString() } } });
using (var validEvent = System.Text.Json.JsonDocument.Parse(eventJson))
{
    var paidSession = WebsiteStripeWebhook.ReadPaidSession(validEvent.RootElement);
    if (paidSession is null || paidSession.CheckoutId != checkoutId || paidSession.AmountPence != 1850)
        throw new Exception("Valid Stripe webhook was not recognised.");
}
foreach (var invalid in new[] { eventJson.Replace("\"paid\"", "\"unpaid\""), eventJson.Replace("\"gbp\"", "\"usd\""), eventJson.Replace("\"livemode\":false", "\"livemode\":true"), eventJson.Replace("cs_test_123", "cs_live_123") })
{
    using var invalidEvent = System.Text.Json.JsonDocument.Parse(invalid);
    if (WebsiteStripeWebhook.ReadPaidSession(invalidEvent.RootElement) is not null)
        throw new Exception("Invalid Stripe webhook was accepted.");
}
Console.WriteLine("Passed paid Stripe webhook event validation checks.");

var liveEventJson = eventJson.Replace("cs_test_123", "cs_live_123").Replace("\"livemode\":false", "\"livemode\":true");
using (var liveEvent = System.Text.Json.JsonDocument.Parse(liveEventJson))
{
    if (WebsiteStripeWebhook.ReadPaidSession(liveEvent.RootElement, true)?.CheckoutId != checkoutId ||
        WebsiteStripeWebhook.ReadPaidSession(liveEvent.RootElement, false) is not null)
        throw new Exception("Live Stripe webhook mode isolation failed.");
}
var liveConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
    ["WebsiteCheckout:StripeMode"] = "live", ["Stripe:SecretKey"] = "sk_live_example",
    ["WebsiteCheckout:StripeWebhookSecret"] = "whsec_example",
    ["WebsiteCheckout:StripeSuccessUrl"] = "https://www.loveanise.co.uk/order-status.html",
    ["WebsiteCheckout:StripeCancelUrl"] = "https://www.loveanise.co.uk/stripe-cancelled.html"
}).Build();
var productionHost = new TestPushEnvironment { EnvironmentName = "Production" };
var stagingHost = new TestPushEnvironment { EnvironmentName = "Staging" };
if (WebsiteStripeMode.From(liveConfig, productionHost)?.Live != true || WebsiteStripeMode.From(liveConfig, stagingHost) is not null)
    throw new Exception("Live Stripe configuration isolation failed.");
liveConfig["Stripe:SecretKey"] = "sk_test_example";
if (WebsiteStripeMode.From(liveConfig, productionHost) is not null)
    throw new Exception("Production accepted test Stripe keys.");
Console.WriteLine("Passed live/test Stripe configuration isolation checks.");
var directSessionJson = System.Text.Json.JsonSerializer.Serialize(new { id = "cs_test_123", livemode = false,
    payment_status = "paid", currency = "gbp", amount_total = 1850, client_reference_id = checkoutId.ToString("D") });
using (var directSession = System.Text.Json.JsonDocument.Parse(directSessionJson))
{
    if (!WebsiteStripeReconciliationWorker.TryPaidSession(directSession.RootElement, checkoutId, new WebsiteStripeMode(false), out var recovered) || recovered?.AmountPence != 1850 ||
        WebsiteStripeReconciliationWorker.TryPaidSession(directSession.RootElement, checkoutId, new WebsiteStripeMode(true), out _) ||
        WebsiteStripeReconciliationWorker.TryPaidSession(directSession.RootElement, Guid.NewGuid(), new WebsiteStripeMode(false), out _))
        throw new Exception("Stripe reconciliation identity or mode checks failed.");
}
Console.WriteLine("Passed direct Stripe reconciliation identity checks.");

var refundSession = System.Text.Json.JsonSerializer.Serialize(new { id = "cs_test_123", livemode = false, currency = "gbp", amount_total = 650,
    payment_intent = new { latest_charge = new { id = "ch_test_123", currency = "gbp", amount = 650, amount_refunded = 650, refunded = true } } });
using (var refundDocument = System.Text.Json.JsonDocument.Parse(refundSession))
{
    if (!OrderQueueEndpoints.HasFullRefund(refundDocument.RootElement, "cs_test_123", 6.50m) ||
        OrderQueueEndpoints.HasFullRefund(refundDocument.RootElement, "cs_test_wrong", 6.50m) ||
        OrderQueueEndpoints.HasFullRefund(refundDocument.RootElement, "cs_test_123", 7m))
        throw new Exception("Stripe refund amount verification failed.");
}
using (var succeeded = System.Text.Json.JsonDocument.Parse("{\"has_more\":false,\"data\":[{\"status\":\"succeeded\",\"currency\":\"gbp\",\"amount\":650}]}"))
using (var pending = System.Text.Json.JsonDocument.Parse("{\"has_more\":false,\"data\":[{\"status\":\"pending\",\"currency\":\"gbp\",\"amount\":650}]}"))
using (var partial = System.Text.Json.JsonDocument.Parse("{\"has_more\":false,\"data\":[{\"status\":\"succeeded\",\"currency\":\"gbp\",\"amount\":300}]}"))
using (var paginated = System.Text.Json.JsonDocument.Parse("{\"has_more\":true,\"data\":[{\"status\":\"succeeded\",\"currency\":\"gbp\",\"amount\":650}]}"))
{
    if (!OrderQueueEndpoints.HasSuccessfulRefunds(succeeded.RootElement, 650) ||
        OrderQueueEndpoints.HasSuccessfulRefunds(pending.RootElement, 650) ||
        OrderQueueEndpoints.HasSuccessfulRefunds(partial.RootElement, 650) ||
        OrderQueueEndpoints.HasSuccessfulRefunds(paginated.RootElement, 650))
        throw new Exception("Stripe refund status verification failed.");
}
Console.WriteLine("Passed full and completed Stripe refund validation checks.");

if (!AnisePOS.Api.Email.GmailConfirmationSender.ValidAddress("customer@example.com") ||
    AnisePOS.Api.Email.GmailConfirmationSender.ValidAddress("customer@example.com\r\nBcc:other@example.com") ||
    AnisePOS.Api.Email.GmailConfirmationSender.ValidAddress("Customer <customer@example.com>") ||
    AnisePOS.Api.Email.GmailConfirmationSender.ValidAddress("not-an-email"))
    throw new Exception("Confirmation email address validation failed.");
Console.WriteLine("Passed confirmation email address validation checks.");

var receipt = AnisePOS.Api.Email.GmailConfirmationSender.Compose(42, "https://example.test/#secret", OrderType.Delivery,
    new DateTimeOffset(2026, 11, 20, 18, 30, 0, TimeSpan.Zero));
if (!receipt.Subject.Contains("#42 accepted") || !receipt.Body.Contains("Estimated delivery: 18:30") ||
    !receipt.Body.Contains("https://example.test/#secret") || !receipt.Body.Contains("expires 24 hours after ordering"))
    throw new Exception("Initial order receipt must include the private tracking link and its expiry.");
var collectionReceipt = AnisePOS.Api.Email.GmailConfirmationSender.Compose(43, "https://example.test/#secret", OrderType.Collection,
    new DateTimeOffset(2026, 11, 20, 18, 15, 0, TimeSpan.Zero));
if (!collectionReceipt.Body.Contains("Estimated collection: 18:15"))
    throw new Exception("Collection confirmation must include the accepted ETA.");

var veganLine = new OrderLine { ProductName = "Vegetable Spring Rolls", VariantName = "Regular", Quantity = 2, IsVegan = true,
    Notes = "Extra crispy", Modifiers = new List<OrderLineModifier> { new() { ModifierName = "Chilli sauce" } } };
var chickenLine = new OrderLine { ProductName = "Chicken Chow Mein", VariantName = "Regular", Quantity = 1 };
var kitchenTicket = AnisePOS.Api.Printing.ReceiptPrinter.Format(new Order { OrderNumber = 7, Lines = new List<OrderLine> { veganLine, chickenLine } }, true);
var kitchenText = System.Text.Encoding.ASCII.GetString(kitchenTicket);
if (!kitchenText.Contains("2 x [V] Vegetable Spring Rolls") || !kitchenText.Contains("1 x Chicken Chow Mein") ||
    !kitchenText.Contains("  + Chilli sauce") || !kitchenText.Contains("  NOTE: Extra crispy") || kitchenText.Contains("? Vegetable"))
    throw new Exception("Kitchen receipt vegan marker or line details failed.");
var customerReceipt = System.Text.Encoding.ASCII.GetString(AnisePOS.Api.Printing.ReceiptPrinter.Format(new Order { OrderNumber = 8, Lines = new List<OrderLine> { veganLine } }, false));
if (customerReceipt.Contains("[V]") || !customerReceipt.Contains("2 x Vegetable Spring Rolls"))
    throw new Exception("Customer receipt vegan formatting changed unexpectedly.");
Console.WriteLine("Passed vegan kitchen receipt and customer receipt compatibility checks.");

var sectorSettings = new DeliverySettings { RadiusMiles = 1, Fee = 2m, SectorsJson = System.Text.Json.JsonSerializer.Serialize(new[] { new DeliverySector("NW1 8", 3.50m), new DeliverySector("N1 1", 0m) }) };
var nearby = DeliverySettingsForWebsite.Decide(sectorSettings, "nw1 8et", 51.5434592, -0.1492259);
var lowerSector = DeliverySettingsForWebsite.Decide(sectorSettings, "N1 1AA", 51.5434592, -0.1492259);
var unmatched = DeliverySettingsForWebsite.Decide(sectorSettings, "NW1 9AA", 51.5434592, -0.1492259);
var outside = DeliverySettingsForWebsite.Decide(sectorSettings, "NW1 8ET", 51.5724, -0.1492259);
if (DeliverySettingsForWebsite.NormalizeSector(" nw1 8 ") != "NW1 8" ||
    DeliverySettingsForWebsite.SectorFromPostcode("nw18et") != "NW1 8" ||
    nearby?.Fee != 3.50m || nearby.RequiresApproval || lowerSector?.Fee != 2m || unmatched?.Fee != 2m ||
    outside?.Fee != 3.50m || !outside.RequiresApproval ||
    DeliverySettingsForWebsite.Decide(sectorSettings, "NW1 8ET", 51.60, -0.1492259) != null)
    throw new Exception("Delivery distance, sector and approval checks failed.");
Console.WriteLine("Passed delivery distance, postcode fee and approval checks.");

var permissionError = CommerceEndpoints.ParseGoogleRouteError("{\"error\":{\"status\":\"PERMISSION_DENIED\",\"message\":\"Requests from IP 203.0.113.10 to NW3 3AX are blocked\"}}");
if (permissionError.Status != "PERMISSION_DENIED" ||
    CommerceEndpoints.ClassifyGoogleRouteError(403, permissionError.Status) != "ApiRestriction" ||
    !CommerceEndpoints.SanitizeGoogleRouteMessage(permissionError.Message, "NW3 3AX", null).Contains("[redacted-destination]") ||
    CommerceEndpoints.ClassifyGoogleRouteError(400, "INVALID_ARGUMENT") != "InvalidRequest" ||
    CommerceEndpoints.ClassifyGoogleRouteError(429, "RESOURCE_EXHAUSTED") != "Quota" ||
    CommerceEndpoints.ClassifyGoogleRouteError(503, "UNAVAILABLE") != "NetworkOrGoogleUnavailable")
    throw new Exception("Google Routes error parsing or categorisation checks failed.");
if (!System.Text.Json.JsonSerializer.Serialize(new DrivingRouteRequest("NW3 3AX", null, null)).Contains("NW3 3AX") ||
    !System.Text.Json.JsonSerializer.Serialize(new DrivingRouteRequest(null, null, "ChIJtest-place-id")).Contains("ChIJtest-place-id"))
    throw new Exception("Google Routes postcode and Place ID request checks failed.");
Console.WriteLine("Passed Google Routes error, postcode-only and Place ID request checks.");
var bandSettings = new DeliverySettings { RadiusMiles = 2, Fee = 4m, RadiusBandsJson = System.Text.Json.JsonSerializer.Serialize(new[] { new DeliveryRadiusBand(2, 4m), new DeliveryRadiusBand(1, 2m) }), SectorsJson = sectorSettings.SectorsJson };
var innerBand = DeliverySettingsForWebsite.Decide(bandSettings, "NW1 9AA", 51.5434592, -0.1492259);
var outerBand = DeliverySettingsForWebsite.Decide(bandSettings, "NW1 9AA", 51.565, -0.1492259);
var bandApproval = DeliverySettingsForWebsite.Decide(bandSettings, "NW1 9AA", 51.58, -0.1492259);
var bandSector = DeliverySettingsForWebsite.Decide(bandSettings, "NW1 8ET", 51.5434592, -0.1492259);
if (innerBand?.Fee != 2m || innerBand.RequiresApproval || outerBand?.Fee != 4m || outerBand.RequiresApproval || bandApproval?.Fee != 4m || !bandApproval.RequiresApproval || bandSector?.Fee != 3.50m ||
    DeliverySettingsForWebsite.ValidBands([new(1, 2), new(1, 3)]) || DeliverySettingsForWebsite.ValidBands([new(4, 2)]) ||
    DeliverySettingsForWebsite.Decide(new DeliverySettings { RadiusMiles = 1, Fee = 2, RadiusBandsJson = "invalid" }, "NW1 9AA", 51.5434592, -0.1492259) != null)
    throw new Exception("Delivery radius bands failed.");
Console.WriteLine("Passed delivery radius bands, validation and sector interaction checks.");
if (!CommerceEndpoints.ValidPosEstimate(OrderType.Collection, null) || !CommerceEndpoints.ValidPosEstimate(OrderType.Collection, 10) ||
    !CommerceEndpoints.ValidPosEstimate(OrderType.Delivery, 30) || CommerceEndpoints.ValidPosEstimate(OrderType.Collection, 11) ||
    !CommerceEndpoints.ValidPosEstimate(OrderType.Delivery, 15) || CommerceEndpoints.ValidPosEstimate(OrderType.Delivery, 16) || CommerceEndpoints.ValidPosEstimate(OrderType.Collection, 0) ||
    CommerceEndpoints.ValidPosEstimate(OrderType.Delivery, 1450)) throw new Exception("POS estimate validation failed.");
Console.WriteLine("Passed POS estimated time validation checks.");
var days = Enumerable.Range(0, 7).Select(day => new OpeningDay(day, day is not (1 or 2), "09:00", "22:00")).ToList();
var hours = new OrderingSettings(true, days, false, null, [], null, []);
var mondayOpen = DateTimeOffset.Parse("2026-06-01T08:00:00Z");
var mondayClosed = DateTimeOffset.Parse("2026-06-01T21:00:00Z");
if (!OrderingAvailability.Valid(hours) || !OrderingAvailability.Evaluate(hours, mondayOpen).Accepting ||
    OrderingAvailability.Evaluate(hours, mondayClosed).NextOpenAt != DateTimeOffset.Parse("2026-06-02T08:00:00Z")) throw new Exception("London opening hours failed.");
var paused = hours with { Paused = true, PauseReasons = ["We are busy"], PauseUntil = mondayOpen.AddMinutes(30) };
if (OrderingAvailability.Evaluate(paused, mondayOpen).NextOpenAt != mondayOpen.AddMinutes(30) || OrderingAvailability.Evaluate(paused, mondayOpen).Reason != "We are busy") throw new Exception("Ordering pause failed.");
var holiday = hours with { Closures = [new ClosurePeriod(mondayOpen, mondayOpen.AddHours(2), ["Holiday"], null)] };
if (OrderingAvailability.Evaluate(holiday, mondayOpen).NextOpenAt != mondayOpen.AddHours(2)) throw new Exception("Scheduled closure failed.");
if (!OrderingAvailability.Evaluate(OrderingAvailability.Parse(null), mondayOpen).Accepting || OrderingAvailability.Evaluate(OrderingAvailability.Parse("invalid"), mondayOpen).Accepting) throw new Exception("Ordering settings fallback failed.");
var overnight = hours with { Hours = days.Select(d => d.Day == 1 ? d with { Open = "22:00", Close = "02:00" } : d with { Closed = true }).ToList() };
if (!OrderingAvailability.Evaluate(overnight, DateTimeOffset.Parse("2026-06-02T00:30:00Z")).Accepting || OrderingAvailability.Evaluate(overnight, DateTimeOffset.Parse("2026-06-02T01:00:00Z")).Accepting) throw new Exception("Overnight opening hours failed.");
Console.WriteLine("Passed ordering opening hours, pauses and scheduled closures checks.");
if (!StaffPushEndpoints.ValidEndpoint("https://web.push.apple.com/Qabcdef") ||
    !StaffPushEndpoints.ValidEndpoint("https://fcm.googleapis.com/fcm/send/test") ||
    StaffPushEndpoints.ValidEndpoint("http://web.push.apple.com/test") ||
    StaffPushEndpoints.ValidEndpoint("https://web.push.apple.com.evil.example/test") ||
    StaffPushEndpoints.ValidEndpoint("https://localhost/test") ||
    StaffPushEndpoints.ValidEndpoint("https://web.push.apple.com:8443/test") ||
    StaffPushEndpoints.ValidEndpoint("https://user@web.push.apple.com/test"))
    throw new Exception("Staff notification endpoint validation failed.");
Console.WriteLine("Passed staff push endpoint validation checks.");
var pushRoot = Path.Combine(Path.GetTempPath(), "anise-push-test-" + Guid.NewGuid().ToString("N"));
var pushEnvironment = new TestPushEnvironment { ContentRootPath = Path.Combine(pushRoot, "site") };
var pushProtection = new EphemeralDataProtectionProvider();
var pushKeys = new PushKeyStore(pushEnvironment, pushProtection).Get();
var reopenedKeys = new PushKeyStore(pushEnvironment, pushProtection).Get();
if (string.IsNullOrEmpty(pushKeys.PublicKey) || string.IsNullOrEmpty(pushKeys.PrivateKey) || pushKeys.PublicKey != reopenedKeys.PublicKey ||
    File.ReadAllText(Path.Combine(pushRoot, "data", "staff-push-keys.protected")).Contains(pushKeys.PrivateKey))
    throw new Exception("Staff push keys were not stored securely and persistently.");
Console.WriteLine("Passed protected staff push key persistence checks.");
using var subscriptionKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
var publicPoint = subscriptionKey.ExportParameters(false).Q;
static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
var clientKey = Base64Url(new byte[] { 4 }.Concat(publicPoint.X!).Concat(publicPoint.Y!).ToArray());
var pushHandler = new TestPushHandler();
using (var pushHttp = new HttpClient(pushHandler))
using (var pushClient = new WebPushClient(pushHttp))
{
    await pushClient.SendNotificationAsync(new PushSubscription("https://fcm.googleapis.com/fcm/send/test", clientKey, Base64Url(RandomNumberGenerator.GetBytes(16))),
        "{\"title\":\"New website order\"}", new Dictionary<string, object> { ["vapidDetails"] = pushKeys, ["TTL"] = 300 });
}
if (!pushHandler.WasSent) throw new Exception("Staff push message was not encrypted and sent.");
Console.WriteLine("Passed encrypted staff push message construction checks.");

var testSessionUnrefunded = "{\"id\":\"cs_test_123\",\"livemode\":false,\"payment_status\":\"paid\",\"currency\":\"gbp\",\"amount_total\":650,\"payment_intent\":{\"latest_charge\":{\"id\":\"ch_123\",\"currency\":\"gbp\",\"amount\":650,\"amount_refunded\":0,\"refunded\":false}}}";
var testSessionRefunded = testSessionUnrefunded.Replace("\"amount_refunded\":0,\"refunded\":false", "\"amount_refunded\":650,\"refunded\":true");
var noRefunds = "{\"has_more\":false,\"data\":[]}";
var doneRefunds = "{\"has_more\":false,\"data\":[{\"status\":\"succeeded\",\"currency\":\"gbp\",\"amount\":650}]}";
var pendingRefunds = doneRefunds.Replace("succeeded", "pending");
var paymentId = Guid.NewGuid();
var successfulHandler = new RefundHandler(testSessionUnrefunded, noRefunds, "{}", testSessionRefunded, doneRefunds);
var successfulResult = await WebsiteStripeRefund.EnsureRefundedAsync(new HttpClient(successfulHandler), "sk_test_example", "cs_test_123", paymentId, 6.50m, CancellationToken.None);
if (successfulResult != WebsiteStripeRefund.Result.Complete || successfulHandler.PostCount != 1 || successfulHandler.IdempotencyKey != "website-decline-refund-" + paymentId.ToString("N")) throw new Exception("Automatic Stripe refund failed.");
var liveRefundHandler = new RefundHandler(testSessionUnrefunded.Replace("cs_test_", "cs_live_").Replace("\"livemode\":false", "\"livemode\":true"), noRefunds, "{}", testSessionRefunded.Replace("cs_test_", "cs_live_").Replace("\"livemode\":false", "\"livemode\":true"), doneRefunds);
if (await WebsiteStripeRefund.EnsureRefundedAsync(new HttpClient(liveRefundHandler), "sk_live_example", "cs_live_123", paymentId, 6.50m, CancellationToken.None, true) != WebsiteStripeRefund.Result.Complete || liveRefundHandler.PostCount != 1)
    throw new Exception("Live website refund validation failed.");
var retryHandler = new RefundHandler(testSessionRefunded, doneRefunds);
if (await WebsiteStripeRefund.EnsureRefundedAsync(new HttpClient(retryHandler), "sk_test_example", "cs_test_123", paymentId, 6.50m, CancellationToken.None) != WebsiteStripeRefund.Result.Complete || retryHandler.PostCount != 0) throw new Exception("Refund reconciliation repeated a refund.");
var pendingHandler = new RefundHandler(testSessionUnrefunded, pendingRefunds);
if (await WebsiteStripeRefund.EnsureRefundedAsync(new HttpClient(pendingHandler), "sk_test_example", "cs_test_123", paymentId, 6.50m, CancellationToken.None) != WebsiteStripeRefund.Result.Pending || pendingHandler.PostCount != 0) throw new Exception("Pending refund was repeated.");
var failedHandler = new RefundHandler(testSessionUnrefunded, noRefunds);
if (await WebsiteStripeRefund.EnsureRefundedAsync(new HttpClient(failedHandler), "sk_test_example", "cs_test_123", paymentId, 6.50m, CancellationToken.None) != WebsiteStripeRefund.Result.Unconfirmed || failedHandler.PostCount != 1) throw new Exception("Failed refund was not handled safely.");
var mismatchHandler = new RefundHandler(testSessionUnrefunded.Replace("\"amount_total\":650", "\"amount_total\":500"));
if (await WebsiteStripeRefund.EnsureRefundedAsync(new HttpClient(mismatchHandler), "sk_test_example", "cs_test_123", paymentId, 6.50m, CancellationToken.None) != WebsiteStripeRefund.Result.Unconfirmed || mismatchHandler.PostCount != 0) throw new Exception("Incorrect amount was refunded.");
Console.WriteLine("Passed automatic refund, retry, pending, failure and amount validation checks.");

var printerAddresses = new Dictionary<string, bool>
{
    ["192.168.1.233"] = true,
    ["10.0.0.50"] = true,
    ["255.255.255.255"] = false,
    ["192.168.1.999"] = false,
    [""] = false,
    ["http://192.168.1.233"] = false
};
foreach (var (address, expected) in printerAddresses)
    if (PrinterAddressValidation.Validate(address, out _) != expected)
        throw new Exception($"Printer address validation failed for '{address}'.");
Console.WriteLine("Passed receipt printer IPv4 validation checks.");

sealed class RefundHandler(params string[] responses) : HttpMessageHandler
{
    private readonly Queue<string> _responses = new(responses);
    public int PostCount { get; private set; }
    public string? IdempotencyKey { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post) { PostCount++; IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single(); }
        if (_responses.Count == 0) throw new HttpRequestException("Simulated connection failure");
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(_responses.Dequeue()) });
    }
}

sealed class TestPushEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "test";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public string EnvironmentName { get; set; } = "Test";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

sealed class TestPushHandler : HttpMessageHandler
{
    public bool WasSent { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        WasSent = request.Headers.Authorization is not null && body.Length > 0 && request.Headers.Contains("TTL");
        return new HttpResponseMessage(System.Net.HttpStatusCode.Created);
    }
}

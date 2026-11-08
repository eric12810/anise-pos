using AnisePOS.Api.Data;
using AnisePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using AnisePOS.Api.Email;
using System.Net.Http.Json;

namespace AnisePOS.Api.Endpoints;

public static class WebsiteOrderEndpoints
{
    public static void MapWebsiteOrderEndpoints(this IEndpointRouteBuilder app)
    {
        // The checkout reference is a random bearer credential; never return customer details here.
        app.MapPost("/api/website/orders/status", async (WebsiteStatusRequest request, AniseDbContext db, HttpResponse response, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "no-store";
            if (request.RequestId == Guid.Empty) return Results.NotFound();
            var reference = request.RequestId.ToString("D");
            var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
            var order = await db.Payments.AsNoTracking()
                .Where(p => p.Provider == "WebsiteCash" && p.ProviderReference == reference && p.Order.CreatedAtUtc >= cutoff)
                .Select(p => new { p.Order.OrderNumber, p.Order.Status, p.Order.OrderType, p.Order.EstimatedAtUtc })
                .SingleOrDefaultAsync(ct);
            if (order is null)
                order = await db.WebsiteCheckouts.AsNoTracking().Where(c => c.RequestId == request.RequestId && c.CreatedAtUtc >= cutoff && c.CompletedOrderId != null)
                    .Join(db.Orders.AsNoTracking(), c => c.CompletedOrderId, o => (Guid?)o.Id,
                        (c, o) => new { o.OrderNumber, o.Status, o.OrderType, o.EstimatedAtUtc }).SingleOrDefaultAsync(ct);
            return order is null ? Results.NotFound() : Results.Ok(order);
        }).RequireCors("PublicWebsiteCheckout").RequireRateLimiting("website-status");

        app.MapPost("/api/website/delivery-quote", async (DeliveryQuoteRequest request, IConfiguration config,
            IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!config.GetValue("WebsiteCheckout:DeliveryQuoteEnabled", true))
                return Results.Problem("Online ordering is not available yet.", statusCode: 503);
            if (!WebsiteCheckoutValidation.ValidPostcode(request.Postcode))
                return Results.BadRequest(new { message = "Enter a valid UK postcode." });
            try
            {
                var client = clients.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(8);
                var location = await client.GetFromJsonAsync<PostcodeLookup>(
                    "https://api.postcodes.io/postcodes/" + Uri.EscapeDataString(request.Postcode!.Trim()), ct);
                var radius = config.GetValue("WebsiteCheckout:DeliveryRadiusMiles", 2.0);
                var fee = config.GetValue("WebsiteCheckout:DeliveryFee", 0m);
                if (!double.IsFinite(radius) || radius < 0 || fee < 0 || fee > 1000)
                    return Results.Problem("Delivery is temporarily unavailable.", statusCode: 503);
                if (location?.Result is null) return Results.BadRequest(new { message = "We could not find that postcode." });
                var eligible = WebsiteCheckoutValidation.IsInDeliveryArea(51.5434592, -0.1492259,
                    location.Result.Latitude, location.Result.Longitude, radius);
                return Results.Ok(new { eligible, deliveryFee = eligible ? fee : (decimal?)null });
            }
            catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
            { return Results.BadRequest(new { message = "We could not find that postcode." }); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            { return Results.Problem("Delivery availability is temporarily unavailable. Please try again.", statusCode: 503); }
        }).RequireCors("PublicWebsiteCheckout").RequireRateLimiting("website-checkout");

        // Disabled until payment verification and fulfilment are implemented.
        app.MapPost("/api/website/stripe-checkout", async (WebsiteCashOrder request, AniseDbContext db,
            IConfiguration config, IHttpClientFactory clients, CancellationToken ct) =>
        {
            if (!config.GetValue("WebsiteCheckout:StripeSessionsEnabled", false))
                return Results.Problem("Card checkout is not available yet.", statusCode: 503);
            var secret = config["Stripe:SecretKey"];
            var successUrl = config["WebsiteCheckout:StripeSuccessUrl"];
            var cancelUrl = config["WebsiteCheckout:StripeCancelUrl"];
            if (secret is null || !secret.StartsWith("sk_test_", StringComparison.Ordinal) ||
                !ValidCheckoutReturnUrl(successUrl) || !ValidCheckoutReturnUrl(cancelUrl))
                return Results.Problem("Stripe test checkout is not configured.", statusCode: 503);
            if (request.RequestId == Guid.Empty || request.Lines is null || request.Lines.Count is < 1 or > 50 ||
                request.Lines.Any(x => x is null || x.Quantity is < 1 or > 99 || x.ModifierIds is null || x.ModifierIds.Count > 50 || (x.Notes?.Length ?? 0) > 500) ||
                request.OrderType is not (OrderType.Collection or OrderType.Delivery) ||
                !WebsiteCheckoutValidation.ValidName(request.Name) || !WebsiteCheckoutValidation.ValidPhone(request.Phone))
                return Results.BadRequest(new { message = "Check your order and contact details." });
            var requestJson = System.Text.Json.JsonSerializer.Serialize(request);
            var requestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestJson)));
            var checkout = await db.WebsiteCheckouts.SingleOrDefaultAsync(x => x.RequestId == request.RequestId, ct);
            if (checkout is not null && checkout.RequestHash != requestHash)
                return Results.Conflict(new { message = "This checkout reference belongs to a different order." });
            if (checkout?.StripeSessionUrl is not null) return Results.Ok(new { url = checkout.StripeSessionUrl });
            if (checkout is null)
            {
            string? address = null;
            decimal fee = 0;
            if (request.OrderType == OrderType.Delivery)
            {
                if (!WebsiteCheckoutValidation.ValidAddressLine(request.AddressLine1, true) ||
                    !WebsiteCheckoutValidation.ValidAddressLine(request.AddressLine2, false) || !WebsiteCheckoutValidation.ValidPostcode(request.Postcode))
                    return Results.BadRequest(new { message = "Enter a delivery address and postcode." });
                var postcode = request.Postcode!.Trim();
                try
                {
                    var client = clients.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(8);
                    var location = await client.GetFromJsonAsync<PostcodeLookup>("https://api.postcodes.io/postcodes/" + Uri.EscapeDataString(postcode), ct);
                    var radius = config.GetValue("WebsiteCheckout:DeliveryRadiusMiles", 2.0);
                    if (location?.Result is null || !WebsiteCheckoutValidation.IsInDeliveryArea(51.5434592, -0.1492259, location.Result.Latitude, location.Result.Longitude, radius))
                        return Results.BadRequest(new { message = "This postcode is outside our delivery area or could not be found." });
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                { return Results.Problem("Delivery availability is temporarily unavailable. Please try again.", statusCode: 503); }
                fee = config.GetValue("WebsiteCheckout:DeliveryFee", 0m);
                if (fee < 0 || fee > 1000) return Results.Problem("Delivery is temporarily unavailable.", statusCode: 503);
                address = string.Join(", ", new[] { request.AddressLine1?.Trim(), request.AddressLine2?.Trim(), postcode }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            var ids = request.Lines.Select(x => x.VariantId).Distinct().ToList();
            var variants = await db.ProductVariants.Include(v => v.Product).ThenInclude(p => p.ModifierGroups)
                .ThenInclude(l => l.ModifierGroup).ThenInclude(g => g.Modifiers)
                .Where(v => ids.Contains(v.Id) && v.IsAvailable && v.Product.IsAvailable && v.Product.Categories.Any(c => c.IsAvailable))
                .ToDictionaryAsync(v => v.Id, ct);
            if (variants.Count != ids.Count) return Results.BadRequest(new { message = "A selected item is no longer available." });
            var order = new Order { OrderType = request.OrderType, Status = OrderStatus.Placed,
                CustomerName = request.Name.Trim(), CustomerPhone = request.Phone.Trim(), DeliveryAddress = address, DeliveryFee = fee };
            foreach (var item in request.Lines)
            {
                var variant = variants[item.VariantId];
                var groups = variant.Product.ModifierGroups.Select(x => x.ModifierGroup).ToList();
                var selected = item.ModifierIds.Distinct().ToList();
                if (selected.Count != item.ModifierIds.Count) return Results.BadRequest(new { message = "Duplicate options are not allowed." });
                var modifiers = groups.SelectMany(g => g.Modifiers).Where(m => selected.Contains(m.Id) && m.IsAvailable).ToList();
                if (modifiers.Count != selected.Count || groups.Any(g => modifiers.Count(m => m.ModifierGroupId == g.Id) < g.MinimumSelections || modifiers.Count(m => m.ModifierGroupId == g.Id) > g.MaximumSelections))
                    return Results.BadRequest(new { message = "Check the options selected for your items." });
                var lineTotal = (variant.Price + modifiers.Sum(m => m.PriceAdjustment)) * item.Quantity;
                if (lineTotal < 0) return Results.BadRequest(new { message = "An item has an invalid price." });
                order.Lines.Add(new OrderLine { ProductId = variant.ProductId, ProductVariantId = variant.Id,
                    ProductName = variant.Product.Name, VariantName = variant.Name, UnitPrice = variant.Price,
                    Quantity = item.Quantity, LineTotal = lineTotal, Notes = string.IsNullOrWhiteSpace(item.Notes) ? null : item.Notes.Trim(),
                    Modifiers = modifiers.Select(m => new OrderLineModifier { ModifierId = m.Id, ModifierName = m.Name, PriceAdjustment = m.PriceAdjustment }).ToList() });
            }
            order.Subtotal = order.Lines.Sum(l => l.LineTotal); order.Total = order.Subtotal + fee;
            if (request.ExpectedTotal is null || request.ExpectedTotal.Value != order.Total)
                return Results.Conflict(new { message = "The order total has changed. Please refresh the menu and review your order." });
            if (order.Total < 0.30m || order.Total > 999999.99m)
                return Results.BadRequest(new { message = "The order total cannot be processed by card." });
            var snapshot = System.Text.Json.JsonSerializer.Serialize(new {
                request, order.OrderType, order.CustomerName, order.CustomerPhone, order.DeliveryAddress,
                order.Subtotal, order.DeliveryFee, order.Total,
                lines = order.Lines.Select(l => new { l.ProductId, l.ProductVariantId, l.ProductName, l.VariantName,
                    l.UnitPrice, l.Quantity, l.LineTotal, l.Notes,
                    modifiers = l.Modifiers.Select(m => new { m.ModifierId, m.ModifierName, m.PriceAdjustment }).ToArray() }).ToArray()
            });
                checkout = new WebsiteCheckout { RequestId = request.RequestId, RequestHash = requestHash, SnapshotJson = snapshot, Total = order.Total };
                db.WebsiteCheckouts.Add(checkout);
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException)
                {
                    db.Entry(checkout).State = EntityState.Detached;
                    checkout = await db.WebsiteCheckouts.SingleOrDefaultAsync(x => x.RequestId == request.RequestId, ct);
                    if (checkout is null) throw;
                }
            }
            if (checkout.RequestHash != requestHash)
                return Results.Conflict(new { message = "This checkout reference belongs to a different order." });
            if (checkout.StripeSessionUrl is not null) return Results.Ok(new { url = checkout.StripeSessionUrl });
            try
            {
                using var stripeRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/checkout/sessions");
                stripeRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
                stripeRequest.Headers.Add("Idempotency-Key", "website-checkout-" + request.RequestId.ToString("D"));
                stripeRequest.Headers.Add("Stripe-Version", "2024-06-20");
                stripeRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                    ["mode"] = "payment", ["payment_method_types[0]"] = "card",
                    ["success_url"] = successUrl!, ["cancel_url"] = cancelUrl!,
                    ["client_reference_id"] = checkout.Id.ToString("D"),
                    ["metadata[website_checkout_id]"] = checkout.Id.ToString("D"),
                    ["line_items[0][price_data][currency]"] = "gbp",
                    ["line_items[0][price_data][product_data][name]"] = "Anise order",
                    ["line_items[0][price_data][unit_amount]"] = decimal.ToInt64(checkout.Total * 100m).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["line_items[0][quantity]"] = "1"
                });
                using var response = await clients.CreateClient().SendAsync(stripeRequest, ct);
                if (!response.IsSuccessStatusCode) return Results.Problem("Could not prepare card checkout. Please retry the same order.", statusCode: 502);
                using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var id = json.RootElement.GetProperty("id").GetString();
                var url = json.RootElement.GetProperty("url").GetString();
                if (id is null || !id.StartsWith("cs_test_", StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "checkout.stripe.com")
                    return Results.Problem("Stripe returned an unexpected response.", statusCode: 502);
                checkout.StripeSessionId = id; checkout.StripeSessionUrl = url; checkout.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { url });
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
            { return Results.Problem("Could not prepare card checkout. Please retry the same order.", statusCode: 502); }
        }).RequireCors("PublicWebsiteCheckout").RequireRateLimiting("website-checkout");

        app.MapPost("/api/website/orders/cash", async (WebsiteCashOrder request, AniseDbContext db,
            IConfiguration config, IHttpClientFactory clients, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            if (!config.GetValue("WebsiteCheckout:Enabled", true))
                return Results.Problem("Online ordering is not available yet.", statusCode: 503);
            var sendConfirmation = config.GetValue("GoogleMail:ConfirmationsEnabled", false) && !string.IsNullOrWhiteSpace(request.Email);
            if (!string.IsNullOrWhiteSpace(request.Email) && !GmailConfirmationSender.ValidAddress(request.Email.Trim()))
                return Results.BadRequest(new { message = "Enter a valid email address for your order confirmation." });
            if (request.RequestId == Guid.Empty || request.Lines is null || request.Lines.Count is < 1 or > 50 ||
                request.Lines.Any(x => x is null || x.Quantity is < 1 or > 99 || x.ModifierIds is null || x.ModifierIds.Count > 50 || (x.Notes?.Length ?? 0) > 500) ||
                request.OrderType is not (OrderType.Collection or OrderType.Delivery) ||
                !WebsiteCheckoutValidation.ValidName(request.Name) || !WebsiteCheckoutValidation.ValidPhone(request.Phone))
                return Results.BadRequest(new { message = "Check your order and contact details." });
            var reference = request.RequestId.ToString("D");
            var existing = await db.Payments.AsNoTracking().Where(p => p.Provider == "WebsiteCash" && p.ProviderReference == reference)
                .Select(p => new { p.Order.OrderNumber, p.Order.Total }).SingleOrDefaultAsync(ct);
            if (existing is not null) return Results.Ok(existing);

            string? address = null;
            decimal fee = 0;
            if (request.OrderType == OrderType.Delivery)
            {
                if (!WebsiteCheckoutValidation.ValidAddressLine(request.AddressLine1, true) ||
                    !WebsiteCheckoutValidation.ValidAddressLine(request.AddressLine2, false) || !WebsiteCheckoutValidation.ValidPostcode(request.Postcode))
                    return Results.BadRequest(new { message = "Enter a delivery address and postcode." });
                var postcode = request.Postcode!.Trim();
                try
                {
                    var client = clients.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(8);
                    var location = await client.GetFromJsonAsync<PostcodeLookup>("https://api.postcodes.io/postcodes/" + Uri.EscapeDataString(postcode), ct);
                    var radius = config.GetValue("WebsiteCheckout:DeliveryRadiusMiles", 2.0);
                    if (location?.Result is null || !WebsiteCheckoutValidation.IsInDeliveryArea(51.5434592, -0.1492259, location.Result.Latitude, location.Result.Longitude, radius))
                        return Results.BadRequest(new { message = "This postcode is outside our delivery area or could not be found." });
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                { return Results.Problem("Delivery availability is temporarily unavailable. Please try again.", statusCode: 503); }
                fee = config.GetValue("WebsiteCheckout:DeliveryFee", 0m);
                if (fee < 0 || fee > 1000) return Results.Problem("Delivery is temporarily unavailable.", statusCode: 503);
                address = string.Join(", ", new[] { request.AddressLine1?.Trim(), request.AddressLine2?.Trim(), postcode }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            var ids = request.Lines.Select(x => x.VariantId).Distinct().ToList();
            var variants = await db.ProductVariants.Include(v => v.Product).ThenInclude(p => p.ModifierGroups)
                .ThenInclude(l => l.ModifierGroup).ThenInclude(g => g.Modifiers)
                .Where(v => ids.Contains(v.Id) && v.IsAvailable && v.Product.IsAvailable && v.Product.Categories.Any(c => c.IsAvailable))
                .ToDictionaryAsync(v => v.Id, ct);
            if (variants.Count != ids.Count) return Results.BadRequest(new { message = "A selected item is no longer available." });
            var order = new Order { OrderType = request.OrderType, Status = OrderStatus.Placed,
                CustomerName = request.Name.Trim(), CustomerPhone = request.Phone.Trim(), DeliveryAddress = address, DeliveryFee = fee };
            foreach (var item in request.Lines)
            {
                var variant = variants[item.VariantId];
                var groups = variant.Product.ModifierGroups.Select(x => x.ModifierGroup).ToList();
                var selected = item.ModifierIds.Distinct().ToList();
                if (selected.Count != item.ModifierIds.Count) return Results.BadRequest(new { message = "Duplicate options are not allowed." });
                var modifiers = groups.SelectMany(g => g.Modifiers).Where(m => selected.Contains(m.Id) && m.IsAvailable).ToList();
                if (modifiers.Count != selected.Count || groups.Any(g => modifiers.Count(m => m.ModifierGroupId == g.Id) < g.MinimumSelections || modifiers.Count(m => m.ModifierGroupId == g.Id) > g.MaximumSelections))
                    return Results.BadRequest(new { message = "Check the options selected for your items." });
                var lineTotal = (variant.Price + modifiers.Sum(m => m.PriceAdjustment)) * item.Quantity;
                if (lineTotal < 0) return Results.BadRequest(new { message = "An item has an invalid price." });
                order.Lines.Add(new OrderLine { ProductId = variant.ProductId, ProductVariantId = variant.Id,
                    ProductName = variant.Product.Name, VariantName = variant.Name, UnitPrice = variant.Price,
                    Quantity = item.Quantity, LineTotal = lineTotal, Notes = string.IsNullOrWhiteSpace(item.Notes) ? null : item.Notes.Trim(),
                    Modifiers = modifiers.Select(m => new OrderLineModifier { ModifierId = m.Id, ModifierName = m.Name, PriceAdjustment = m.PriceAdjustment }).ToList() });
            }
            order.Subtotal = order.Lines.Sum(l => l.LineTotal); order.Total = order.Subtotal + fee;
            if (request.ExpectedTotal is null || request.ExpectedTotal.Value != order.Total)
                return Results.Conflict(new { message = "The order total has changed. Please refresh the menu and review your order." });
            order.Payments.Add(new Payment { Method = PaymentMethod.Cash, Status = PaymentStatus.Pending, Amount = order.Total, Provider = "WebsiteCash", ProviderReference = reference });
            order.StatusHistory.Add(new OrderStatusHistory { Status = OrderStatus.Placed, Note = "Website cash order" });
            db.Orders.Add(order);
            if (sendConfirmation)
            {
                var baseUrl = config["GoogleMail:TrackingBaseUrl"];
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var trackingBase) || trackingBase.Scheme != "https" ||
                    trackingBase.Host is not ("staging.anise.dotnetcloud.co.uk" or "www.loveanise.co.uk" or "loveanise.co.uk") ||
                    !trackingBase.IsDefaultPort || !string.IsNullOrEmpty(trackingBase.UserInfo) ||
                    trackingBase.AbsolutePath != "/order-status.html" || !string.IsNullOrEmpty(trackingBase.Fragment) || !string.IsNullOrEmpty(trackingBase.Query))
                    return Results.Problem("Order confirmation is temporarily unavailable.", statusCode: 503);
                var payload = new ConfirmationPayload(request.Email!.Trim(), baseUrl + "#" + reference);
                db.WebsiteConfirmations.Add(new WebsiteConfirmation { Order = order,
                    ProtectedPayload = protection.CreateProtector("Anise.WebsiteConfirmation.v1").Protect(System.Text.Json.JsonSerializer.Serialize(payload)) });
            }
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                var duplicate = await db.Payments.AsNoTracking().Where(p => p.Provider == "WebsiteCash" && p.ProviderReference == reference)
                    .Select(p => new { p.Order.OrderNumber, p.Order.Total }).SingleOrDefaultAsync(ct);
                if (duplicate is not null) return Results.Ok(duplicate);
                throw;
            }
            return Results.Ok(new { order.OrderNumber, order.Total });
        }).RequireCors("PublicWebsiteCheckout").RequireRateLimiting("website-checkout");
    }
    private static bool ValidCheckoutReturnUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        (uri.Host == "www.loveanise.co.uk" || uri.Host == "loveanise.co.uk") && string.IsNullOrEmpty(uri.UserInfo);
    public sealed record DeliveryQuoteRequest(string? Postcode);
    public sealed record WebsiteStatusRequest(Guid RequestId);
    public sealed record WebsiteCashOrder(Guid RequestId, OrderType OrderType, string Name, string Phone,
        string? AddressLine1, string? AddressLine2, string? Postcode, List<WebsiteOrderLine> Lines, decimal? ExpectedTotal, string? Email = null);
    public sealed record WebsiteOrderLine(Guid VariantId, int Quantity, List<Guid> ModifierIds, string? Notes);
    private sealed record PostcodeLookup(PostcodeCoordinates? Result);
    private sealed record PostcodeCoordinates(double Latitude, double Longitude);
}

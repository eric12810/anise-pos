using AnisePOS.Api.Data;
using AnisePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/ordering-availability", async (AniseDbContext db, CancellationToken ct) =>
            (await OrderingAvailability.Get(db, ct)) is { } settings ? Results.Ok(settings) : Results.Problem("Ordering settings are unavailable.", statusCode: 503)).RequireAuthorization("ManageMenu");
        app.MapPut("/api/admin/ordering-availability", async (OrderingSettings settings, AniseDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            if (!OrderingAvailability.Valid(settings)) return Results.BadRequest(new { message = "Check the opening hours, closure dates and reasons." });
            var row = await db.DeliverySettings.FindAsync(new object[] { 1 }, ct);
            if (row is null) { row = await DeliverySettingsForWebsite.Get(db, config, ct); row.Id = 1; db.DeliverySettings.Add(row); }
            row.OrderingAvailabilityJson = OrderingAvailability.Serialize(settings); await db.SaveChangesAsync(ct); return Results.Ok(settings);
        }).RequireAuthorization("ManageMenu");
        app.MapGet("/api/order-sound", async (AniseDbContext db, CancellationToken ct) =>
        {
            var sound = await db.DeliverySettings.AsNoTracking().Where(x => x.Id == 1).Select(x => x.OrderSound).SingleOrDefaultAsync(ct);
            return Results.Ok(new { sound = sound ?? "classic" });
        }).RequireAuthorization();
        app.MapPut("/api/admin/order-sound", async (HttpRequest httpRequest, AniseDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            OrderSoundRequest? request;
            try { request = await httpRequest.ReadFromJsonAsync<OrderSoundRequest>(cancellationToken: ct); }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { message = "The sound selection could not be read. Refresh this page and try again." }); }
            if (request?.Sound is not ("classic" or "chime" or "bell" or "urgent" or "double" or "rising" or "alarm" or "happy-bells")) return Results.BadRequest(new { message = "Choose a sound from the list." });
            var settings = await db.DeliverySettings.FindAsync(new object[] { 1 }, ct);
            if (settings is null) { settings = await DeliverySettingsForWebsite.Get(db, config, ct); settings.Id = 1; db.DeliverySettings.Add(settings); }
            settings.OrderSound = request.Sound; await db.SaveChangesAsync(ct); return Results.Ok(new { sound = settings.OrderSound });
        }).RequireAuthorization("ManageMenu");
        app.MapGet("/api/printer-settings", async (AniseDbContext db, CancellationToken ct) =>
        {
            var ip = await db.DeliverySettings.AsNoTracking().Where(x => x.Id == 1).Select(x => x.PrinterIpAddress).SingleOrDefaultAsync(ct);
            return Results.Ok(new { ipAddress = ip ?? "192.168.1.234" });
        }).RequireAuthorization();
        app.MapPut("/api/admin/printer-settings", async (PrinterSettingsRequest request, AniseDbContext db, IConfiguration config, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("PrinterSettings");
            var input = request.IpAddress?.Trim();
            if (!PrinterAddressValidation.Validate(input, out var message))
                return Results.BadRequest(new { message });
            var settings = await db.DeliverySettings.FindAsync(new object[] { 1 }, ct);
            if (settings is null) { settings = await DeliverySettingsForWebsite.Get(db, config, ct); settings.Id = 1; db.DeliverySettings.Add(settings); }
            settings.PrinterIpAddress = input;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex)
            {
                logger.LogError(ex, "Could not persist receipt printer address to DeliverySettings.");
                return Results.Problem("The POS server could not store the printer address. Check the server log for the database error.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Ok(new { ipAddress = input });
        }).RequireAuthorization("ManageMenu");
        app.MapGet("/api/admin/delivery-settings", async (AniseDbContext db, IConfiguration config, CancellationToken ct) =>
            Results.Ok(DeliverySettingsForWebsite.Response(await DeliverySettingsForWebsite.Get(db, config, ct)))).RequireAuthorization("ManageMenu");
        app.MapPut("/api/admin/delivery-settings", async (DeliverySettingsRequest request, AniseDbContext db, CancellationToken ct) =>
        {
            if (request.RadiusMiles < 0 || request.RadiusMiles > 3 || request.Fee < 0 || request.Fee > 1000 ||
                decimal.Round(request.RadiusMiles, 2) != request.RadiusMiles || decimal.Round(request.Fee, 2) != request.Fee)
                return Results.BadRequest(new { message = "Enter a radius from 0 to 3 miles and a fee from £0 to £1,000, with at most two decimal places." });
            if (request.Sectors is null || request.Sectors.Count > 500)
                return Results.BadRequest(new { message = "Provide up to 500 postcode sectors." });
            var bands = request.RadiusBands ?? [new DeliveryRadiusBand(request.RadiusMiles, request.Fee)];
            if (!DeliverySettingsForWebsite.ValidBands(bands))
                return Results.BadRequest(new { message = "Add 1 to 50 unique radius bands, each from 0 to 3 miles with a fee from £0 to £1,000. Use at most two decimal places." });
            bands = bands.OrderBy(x => x.RadiusMiles).ToList();
            var sectors = new List<DeliverySector>();
            foreach (var item in request.Sectors)
            {
                if (item is null) return Results.BadRequest(new { message = "Check the postcode sectors and fees." });
                var sector = DeliverySettingsForWebsite.NormalizeSector(item.Sector);
                if (sector is null || item.Fee < 0 || item.Fee > 1000 || decimal.Round(item.Fee, 2) != item.Fee || sectors.Any(x => x.Sector == sector))
                    return Results.BadRequest(new { message = "Check the postcode sectors and fees. Each sector must be unique, with a fee from £0 to £1,000." });
                sectors.Add(new DeliverySector(sector, item.Fee));
            }
            var settings = await db.DeliverySettings.FindAsync(new object[] { 1 }, ct);
            if (settings is null) { settings = new DeliverySettings { Id = 1 }; db.DeliverySettings.Add(settings); }
            settings.RadiusMiles = bands[^1].RadiusMiles; settings.Fee = bands[^1].Fee; settings.Mode = "radius"; settings.SectorsJson = System.Text.Json.JsonSerializer.Serialize(sectors);
            settings.RadiusBandsJson = System.Text.Json.JsonSerializer.Serialize(bands);
            await db.SaveChangesAsync(ct);
            return Results.Ok(DeliverySettingsForWebsite.Response(settings));
        }).RequireAuthorization("ManageMenu");
        app.MapGet("/api/admin/promo-codes", async (AniseDbContext db, IConfiguration config, CancellationToken ct) =>
            Results.Ok(await PromoCodes.Get(db, config, ct))).RequireAuthorization("ManageMenu");
        app.MapPut("/api/admin/promo-codes", async (HttpRequest request, AniseDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            List<PromoCode>? codes;
            try { codes = await request.ReadFromJsonAsync<List<PromoCode>>(cancellationToken: ct); }
            catch (System.Text.Json.JsonException e) { return Results.BadRequest(new { message = "Could not read promo code details. Check the discount and dates (" + e.Path + ")." }); }
            if (codes is null || codes.Count > 100) return Results.BadRequest(new { message = "Provide up to 100 promo codes." });
            var normalized = new List<PromoCode>();
            foreach (var item in codes)
            {
                var code = item?.Code?.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(code) || code.Length > 40 || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
                    item!.Percent <= 0 || item.Percent > 100 || decimal.Round(item.Percent, 2) != item.Percent ||
                    item.MinimumSubtotal < 0 || item.MinimumSubtotal > 999999.99m || decimal.Round(item.MinimumSubtotal, 2) != item.MinimumSubtotal ||
                    (item.StartsAtUtc.HasValue && item.EndsAtUtc.HasValue && item.StartsAtUtc >= item.EndsAtUtc) || normalized.Any(x => x.Code == code))
                    return Results.BadRequest(new { message = "Check each code, discount and date. Codes must be unique and contain only letters, numbers, hyphens or underscores." });
                normalized.Add(item with { Code = code });
            }
            var settings = await db.DeliverySettings.FindAsync(new object[] { 1 }, ct);
            if (settings is null) { settings = await DeliverySettingsForWebsite.Get(db, config, ct); settings.Id = 1; db.DeliverySettings.Add(settings); }
            settings.PromoCodesJson = System.Text.Json.JsonSerializer.Serialize(normalized);
            await db.SaveChangesAsync(ct);
            return Results.Ok(normalized);
        }).RequireAuthorization("ManageMenu");
        app.MapGet("/api/admin/backup", async (AniseDbContext db, CancellationToken ct) =>
        {
            var categories = await db.Categories.AsNoTracking().OrderBy(x => x.DisplayOrder).Select(c => new
            {
                c.Id, c.Name, c.Slug, c.DisplayOrder, c.IsAvailable, c.CreatedAtUtc, c.UpdatedAtUtc,
                Products = c.Products.OrderBy(p => p.DisplayOrder).Select(p => new
                {
                    p.Id, p.Name, p.ChineseName, p.Slug, p.Description, p.DisplayOrder, p.IsAvailable, p.CreatedAtUtc, p.UpdatedAtUtc,
                    Variants = p.Variants.OrderBy(v => v.DisplayOrder).Select(v => new
                    { v.Id, v.Name, v.Sku, v.Price, v.DisplayOrder, v.IsAvailable, v.CreatedAtUtc, v.UpdatedAtUtc }).ToList(),
                    ModifierGroupIds = p.ModifierGroups.OrderBy(x => x.DisplayOrder).Select(x => x.ModifierGroupId).ToList()
                }).ToList()
            }).ToListAsync(ct);
            var modifierGroups = await db.ModifierGroups.AsNoTracking().OrderBy(x => x.Name).Select(g => new
            {
                g.Id, g.Name, g.MinimumSelections, g.MaximumSelections, g.CreatedAtUtc, g.UpdatedAtUtc,
                Modifiers = g.Modifiers.OrderBy(m => m.DisplayOrder).Select(m => new
                { m.Id, m.Name, m.PriceAdjustment, m.DisplayOrder, m.IsAvailable, m.CreatedAtUtc, m.UpdatedAtUtc }).ToList()
            }).ToListAsync(ct);
            var customers = await db.Customers.AsNoTracking().OrderBy(x => x.CreatedAtUtc).Select(c => new
            {
                c.Id, c.FirstName, c.LastName, c.Email, c.Phone, c.CreatedAtUtc, c.UpdatedAtUtc,
                Addresses = c.Addresses.Select(a => new { a.Id, a.Label, a.Line1, a.Line2, a.City, a.Postcode, a.IsDefault }).ToList()
            }).ToListAsync(ct);
            var orders = await db.Orders.AsNoTracking().OrderBy(x => x.OrderNumber).Select(o => new
            {
                o.Id, o.OrderNumber, o.CustomerId, o.OrderType, o.Status, o.CustomerName, o.CustomerPhone,
                o.DeliveryAddress, o.Notes, o.Subtotal, o.DiscountTotal, o.DeliveryFee,
                o.CustomFeeDescription, o.CustomFee, o.Total, o.CreatedAtUtc, o.UpdatedAtUtc,
                Lines = o.Lines.OrderBy(l => l.CreatedAtUtc).Select(l => new
                {
                    l.Id, l.ProductId, l.ProductVariantId, l.ProductName, l.VariantName, l.UnitPrice, l.Quantity, l.LineTotal,
                    Modifiers = l.Modifiers.Select(m => new { m.Id, m.ModifierId, m.ModifierName, m.PriceAdjustment, m.Quantity }).ToList()
                }).ToList(),
                Payments = o.Payments.OrderBy(p => p.CreatedAtUtc).Select(p => new
                { p.Id, p.Method, p.Status, p.Amount, p.Provider, p.ProviderReference, p.PaidAtUtc, p.CreatedAtUtc, p.UpdatedAtUtc }).ToList(),
                StatusHistory = o.StatusHistory.OrderBy(h => h.CreatedAtUtc).Select(h => new
                { h.Id, h.Status, h.Note, h.ChangedBy, h.CreatedAtUtc }).ToList()
            }).ToListAsync(ct);
            var staff = await db.StaffUsers.AsNoTracking().OrderBy(x => x.DisplayName).Select(s => new
            { s.Id, s.Email, s.DisplayName, s.Role, s.IsActive, s.LastSignedInAtUtc, s.CreatedAtUtc, s.UpdatedAtUtc }).ToListAsync(ct);

            return Results.Ok(new { schemaVersion = 1, generatedAtUtc = DateTimeOffset.UtcNow, categories, modifierGroups, customers, orders, staff });
        }).RequireAuthorization("ManageStaff");
    }
}

public sealed record DeliverySector(string Sector, decimal Fee);
public sealed record DeliveryRadiusBand(decimal RadiusMiles, decimal Fee);
public sealed record OrderSoundRequest(string? Sound);
public sealed record PrinterSettingsRequest(string? IpAddress);

public static class PrinterAddressValidation
{
    public static bool Validate(string? input, out string message)
    {
        if (string.IsNullOrWhiteSpace(input) || !System.Net.IPAddress.TryParse(input, out var parsed) ||
            parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || parsed.ToString() != input)
        {
            message = "Enter a valid IPv4 address, for example 192.168.1.234. Do not include http:// or a port.";
            return false;
        }

        var bytes = parsed.GetAddressBytes();
        if (!(bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || (bytes[0] == 192 && bytes[1] == 168)) ||
            bytes[3] == 0 || bytes[3] == 255)
        {
            message = "Enter a usable private-network IPv4 address (10.x.x.x, 172.16–31.x.x or 192.168.x.x).";
            return false;
        }

        message = string.Empty;
        return true;
    }
}

public sealed record DeliverySettingsRequest(decimal RadiusMiles, decimal Fee, string? Mode, List<DeliverySector>? Sectors, List<DeliveryRadiusBand>? RadiusBands = null);

public static class DeliverySettingsForWebsite
{
    public static string? NormalizeSector(string? value)
    {
        var compact = (value ?? "").Trim().Replace(" ", "").ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(compact, @"^[A-Z]{1,2}[0-9][A-Z0-9]?[0-9]$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return null;
        return compact[..^1] + " " + compact[^1];
    }
    public static string? SectorFromPostcode(string? postcode)
    {
        if (!WebsiteCheckoutValidation.ValidPostcode(postcode)) return null;
        var compact = postcode!.Replace(" ", "").ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(compact, @"^[A-Z]{1,2}[0-9][A-Z0-9]?[0-9][A-Z]{2}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return null;
        return NormalizeSector(compact[..^2]);
    }
    public static List<DeliverySector> Sectors(DeliverySettings settings) => System.Text.Json.JsonSerializer.Deserialize<List<DeliverySector>>(settings.SectorsJson) ?? [];
    public static bool ValidBands(IReadOnlyList<DeliveryRadiusBand> bands) => bands.Count is > 0 and <= 50 &&
        bands.All(x => x is not null && x.RadiusMiles >= 0 && x.RadiusMiles <= 3 && x.Fee >= 0 && x.Fee <= 1000 &&
            decimal.Round(x.RadiusMiles, 2) == x.RadiusMiles && decimal.Round(x.Fee, 2) == x.Fee) &&
        bands.Select(x => x.RadiusMiles).Distinct().Count() == bands.Count;
    public static List<DeliveryRadiusBand>? RadiusBands(DeliverySettings settings)
    {
        try {
            var bands = System.Text.Json.JsonSerializer.Deserialize<List<DeliveryRadiusBand>>(settings.RadiusBandsJson);
            if (bands is null) return null;
            if (bands.Count == 0) bands.Add(new(settings.RadiusMiles, settings.Fee));
            return ValidBands(bands) ? bands.OrderBy(x => x.RadiusMiles).ToList() : null;
        } catch (System.Text.Json.JsonException) { return null; }
    }
    public static object Response(DeliverySettings settings) => new { settings.RadiusMiles, settings.Fee, settings.Mode, sectors = Sectors(settings), radiusBands = RadiusBands(settings) };
    public sealed record DeliveryDecision(decimal Fee, bool RequiresApproval);
    public static DeliveryDecision? Decide(DeliverySettings settings, string postcode, double latitude, double longitude)
    {
        if (settings.RadiusMiles < 0 || settings.RadiusMiles > 3 || settings.Fee < 0 || settings.Fee > 1000) return null;
        var bands = RadiusBands(settings);
        if (bands is null) return null;
        if (!WebsiteCheckoutValidation.IsInDeliveryArea(51.5434592, -0.1492259, latitude, longitude, 3)) return null;
        var sector = SectorFromPostcode(postcode);
        var sectorFee = sector is null ? null : Sectors(settings).FirstOrDefault(x => x.Sector == sector)?.Fee;
        var matched = bands.FirstOrDefault(x => WebsiteCheckoutValidation.IsInDeliveryArea(51.5434592, -0.1492259, latitude, longitude, (double)x.RadiusMiles));
        var fee = Math.Max((matched ?? bands[^1]).Fee, sectorFee ?? 0m);
        var requiresApproval = matched is null;
        return new DeliveryDecision(fee, requiresApproval);
    }
    public static async Task<DeliverySettings> Get(AniseDbContext db, IConfiguration config, CancellationToken ct) =>
        await db.DeliverySettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct) ?? new DeliverySettings
        { RadiusMiles = config.GetValue("WebsiteCheckout:DeliveryRadiusMiles", 2m), Fee = config.GetValue("WebsiteCheckout:DeliveryFee", 0m) };
}

public sealed record PromoCode(string Code, bool Enabled, decimal Percent, DateTimeOffset? StartsAtUtc = null, DateTimeOffset? EndsAtUtc = null, decimal MinimumSubtotal = 0, bool OncePerPhone = false);
public static class PromoCodes
{
    public static string NormalizePhone(string value)
    {
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("0044")) digits = digits[4..];
        else if (digits.StartsWith("44")) digits = digits[2..];
        else return digits;
        if (!digits.StartsWith('0')) digits = "0" + digits;
        return digits;
    }
    public static Task<bool> Used(AniseDbContext db, string code, string phone, CancellationToken ct)
    {
        var legacy = phone.StartsWith('0') ? "0" + phone : phone;
        return db.PromoRedemptions.AnyAsync(x => x.Code == code && (x.Phone == phone || x.Phone == legacy), ct);
    }

    public static async Task<List<PromoCode>> Get(AniseDbContext db, IConfiguration config, CancellationToken ct)
    {
        var json = await db.DeliverySettings.AsNoTracking().Where(x => x.Id == 1).Select(x => x.PromoCodesJson).SingleOrDefaultAsync(ct);
        if (json is not null) return System.Text.Json.JsonSerializer.Deserialize<List<PromoCode>>(json) ?? [];
        return config.GetSection("WebsiteCheckout:PromoCodes").GetChildren().Select(x => new PromoCode(
            x["Code"] ?? "", x.GetValue("Enabled", false), x.GetValue<decimal>("Percent"),
            DateTimeOffset.TryParse(x["StartsAtUtc"], out var start) ? start : null,
            DateTimeOffset.TryParse(x["EndsAtUtc"], out var end) ? end : null, x.GetValue<decimal>("MinimumSubtotal"))).ToList();
    }
    public static async Task<PromoCode?> Find(AniseDbContext db, IConfiguration config, string? code, CancellationToken ct)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 40 || !normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        var promo = (await Get(db, config, ct)).FirstOrDefault(x => string.Equals(x.Code, normalized, StringComparison.OrdinalIgnoreCase));
        var now = DateTimeOffset.UtcNow;
        return promo is not null && promo.Enabled && promo.Percent > 0 && promo.Percent <= 100 &&
            (!promo.StartsAtUtc.HasValue || now >= promo.StartsAtUtc) && (!promo.EndsAtUtc.HasValue || now < promo.EndsAtUtc) ? promo : null;
    }
}

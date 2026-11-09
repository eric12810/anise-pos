using System.Text.Json;
using AnisePOS.Api.Data;
using AnisePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Endpoints;

public static class PromotionEndpoints
{
    public static bool EligibleItemDiscount(PromotionSettings settings, DateTimeOffset now, bool existing, OrderType orderType) =>
        settings.Enabled && settings.Kind is PromotionKind.ItemDiscount or PromotionKind.CategoryDiscount &&
        settings.Percent > 0 && settings.Percent <= 100 && settings.MinimumSubtotal >= 0 &&
        (!settings.StartsAtUtc.HasValue || settings.StartsAtUtc <= now) && (!settings.EndsAtUtc.HasValue || now < settings.EndsAtUtc) &&
        (settings.Audience == PromotionAudience.All || settings.Audience == PromotionAudience.Existing && existing || settings.Audience == PromotionAudience.New && !existing) &&
        (settings.Fulfilment == PromotionFulfilment.Both || settings.Fulfilment == PromotionFulfilment.Collection && orderType == OrderType.Collection || settings.Fulfilment == PromotionFulfilment.Delivery && orderType == OrderType.Delivery);

    public static void MapPromotionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/promotions", async (AniseDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Promotions.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).Select(x => new { x.Id, x.SettingsJson, HasImage = x.ImageData != null }).ToListAsync(ct)))
            .RequireAuthorization("ManageMenu");
        app.MapPost("/api/admin/promotions/{id:guid}", async (Guid id, PromotionSettings settings, AniseDbContext db, CancellationToken ct) =>
        {
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(settings.Name) || settings.Name.Trim().Length > 120 || settings.Name.Any(char.IsControl) ||
                (settings.Description?.Length ?? 0) > 500 || settings.MinimumSubtotal < 0 || settings.MinimumSubtotal > 999999.99m ||
                decimal.Round(settings.MinimumSubtotal, 2) != settings.MinimumSubtotal || !Enum.IsDefined(settings.Kind) ||
                !Enum.IsDefined(settings.Audience) || !Enum.IsDefined(settings.Fulfilment) ||
                settings.StartsAtUtc >= settings.EndsAtUtc && settings.StartsAtUtc.HasValue && settings.EndsAtUtc.HasValue ||
                settings.Percent < 0 || settings.Percent > 100 || decimal.Round(settings.Percent, 2) != settings.Percent || settings.BundlePrice < 0 || settings.BundlePrice > 999999.99m || decimal.Round(settings.BundlePrice, 2) != settings.BundlePrice ||
                settings.BundleItems is null || settings.ChoiceProducts is { Count: > 100 } || settings.ChoiceQuantity < 0 || settings.ChoiceQuantity > 99 || (settings.ChoiceQuantity > 0 && (settings.ChoiceProducts?.Count ?? 0) == 0) || settings.BundleItems.Count > 30 || settings.BundleItems.Any(x => x.Quantity < 1 || x.Quantity > 99))
                return Results.BadRequest(new { message = "Check the promotion details and dates." });
            if ((settings.Kind is PromotionKind.ItemDiscount or PromotionKind.CategoryDiscount) && settings.Percent <= 0 ||
                (settings.Kind is PromotionKind.ItemDiscount or PromotionKind.FreeItem) && !settings.ProductId.HasValue ||
                settings.Kind == PromotionKind.CategoryDiscount && !settings.CategoryId.HasValue ||
                settings.Kind == PromotionKind.Bundle && (settings.BundleItems.Count == 0 && settings.ChoiceQuantity == 0 || settings.BundleItems.Select(x => x.ProductId).Distinct().Count() != settings.BundleItems.Count))
                return Results.BadRequest(new { message = "Choose the items or category and a valid offer for this promotion." });
            var productIds = settings.BundleItems.Select(x => x.ProductId).Concat(settings.ChoiceProducts ?? []).Concat(settings.ProductId.HasValue ? new[] { settings.ProductId.Value } : Array.Empty<Guid>()).Distinct().ToList();
            if (productIds.Count != await db.Products.CountAsync(x => productIds.Contains(x.Id), ct) ||
                settings.CategoryId.HasValue && !await db.Categories.AnyAsync(x => x.Id == settings.CategoryId, ct))
                return Results.BadRequest(new { message = "A selected item or category no longer exists." });
            var entity = await db.Promotions.FindAsync(new object[] { id }, ct);
            if (entity is null) { entity = new Promotion { Id = id }; db.Promotions.Add(entity); }
            entity.SettingsJson = JsonSerializer.Serialize(settings with { Name = settings.Name.Trim() }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { entity.Id });
        }).RequireAuthorization("ManageMenu");
        app.MapPost("/api/admin/promotions/{id:guid}/image", async (Guid id, HttpRequest request, AniseDbContext db, CancellationToken ct) =>
        {
            const int limit = 5 * 1024 * 1024;
            if (request.ContentLength > limit) return Results.BadRequest(new { message = "Image must be 5 MB or smaller." });
            var entity = await db.Promotions.FindAsync(new object[] { id }, ct);
            if (entity is null) return Results.NotFound();
            using var buffer = new MemoryStream(); var chunk = new byte[81920]; int count;
            while ((count = await request.Body.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + count > limit) return Results.BadRequest(new { message = "Image must be 5 MB or smaller." }); buffer.Write(chunk, 0, count); }
            var data = buffer.ToArray();
            var type = data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255 ? "image/jpeg" :
                data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png" :
                data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8) ? "image/webp" : null;
            if (type is null) return Results.BadRequest(new { message = "Choose a JPEG, PNG or WebP image." });
            entity.ImageData = data; entity.ImageContentType = type; await db.SaveChangesAsync(ct); return Results.NoContent();
        }).RequireAuthorization("ManageMenu");
        app.MapPost("/api/website/promotions", async (PromotionDisplayRequest request, AniseDbContext db, HttpResponse response, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "no-store";
            bool existing = request.IsReturningCustomer;
            var now = DateTimeOffset.UtcNow;
            var rows = await db.Promotions.AsNoTracking().Select(x => new { x.Id, x.SettingsJson, HasImage = x.ImageData != null }).ToListAsync(ct);
            var result = new List<object>();
            var targetedOnHome = false; var targetedOnOrder = false;
            var productNames = await db.Products.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var categoryNames = await db.Categories.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            foreach (var row in rows)
            {
                PromotionSettings? settings;
                try { settings = JsonSerializer.Deserialize<PromotionSettings>(row.SettingsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                catch (JsonException) { continue; }
                if (settings is null || !settings.Enabled || !settings.ShowOnHome && !settings.ShowOnOrder ||
                    settings.StartsAtUtc.HasValue && settings.StartsAtUtc > now || settings.EndsAtUtc.HasValue && settings.EndsAtUtc <= now) continue;
                if (settings.Audience != PromotionAudience.All) { targetedOnHome |= settings.ShowOnHome; targetedOnOrder |= settings.ShowOnOrder; }
                if (settings.Audience == PromotionAudience.New && existing != false || settings.Audience == PromotionAudience.Existing && existing != true) continue;
                var details = new List<string>();
                if (settings.Kind == PromotionKind.FreeItem && settings.ProductId.HasValue && productNames.TryGetValue(settings.ProductId.Value, out var freeName)) details.Add("Add " + freeName + " to your basket. One item is free per order.");
                if (settings.Kind == PromotionKind.ItemDiscount && settings.ProductId.HasValue && productNames.TryGetValue(settings.ProductId.Value, out var itemName)) details.Add($"{settings.Percent:0.##}% off {itemName}.");
                if (settings.Kind == PromotionKind.CategoryDiscount && settings.CategoryId.HasValue && categoryNames.TryGetValue(settings.CategoryId.Value, out var categoryName)) details.Add($"{settings.Percent:0.##}% off {categoryName}.");
                if (settings.Kind == PromotionKind.Bundle)
                {
                    if (settings.BundleItems?.Count > 0) details.Add("Add " + string.Join(", ", settings.BundleItems.Select(x => x.Quantity + " × " + productNames.GetValueOrDefault(x.ProductId, "dish"))) + ".");
                    if (settings.ChoiceQuantity > 0 && settings.ChoiceProducts?.Count > 0) details.Add("Choose " + settings.ChoiceQuantity + " from: " + string.Join(", ", settings.ChoiceProducts.Select(x => productNames.GetValueOrDefault(x, "dish"))) + ".");
                    details.Add($"Bundle price £{settings.BundlePrice:F2}; one bundle per order.");
                }
                if (settings.MinimumSubtotal > 0) details.Add($"Minimum food subtotal £{settings.MinimumSubtotal:F2}.");
                result.Add(new { row.Id, settings.Name, settings.Description, Details = string.Join(" ", details), settings.ShowOnHome, settings.ShowOnOrder, settings.Fulfilment,
                    ImageUrl = row.HasImage ? "/api/website/promotions/" + row.Id + "/image" : null });
            }
            return Results.Ok(new { offers = result, targetedOnHome, targetedOnOrder });
        }).RequireCors("PublicWebsiteCheckout").RequireRateLimiting("website-offers");
        app.MapPost("/api/website/item-discounts", async (ItemDiscountRequest request, AniseDbContext db, HttpResponse response, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "no-store";
            if (request.OrderType is not (OrderType.Collection or OrderType.Delivery)) return Results.BadRequest();
            var rows = await db.Promotions.AsNoTracking().Select(x => new { x.Id, x.SettingsJson }).ToListAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var rules = new List<(Guid Id, PromotionSettings Settings)>();
            foreach (var row in rows)
            {
                PromotionSettings? settings;
                try { settings = JsonSerializer.Deserialize<PromotionSettings>(row.SettingsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
                catch (JsonException) { continue; }
                if (settings is not null && EligibleItemDiscount(settings, now, request.IsReturningCustomer, request.OrderType)) rules.Add((row.Id, settings));
            }
            if (rules.Count == 0) return Results.Ok(Array.Empty<object>());
            var products = await db.Products.AsNoTracking().Where(x => x.IsAvailable)
                .Select(x => new { x.Id, Categories = x.Categories.Select(c => c.Id).ToList() }).ToListAsync(ct);
            var discounts = products.SelectMany(p => rules.Where(r => r.Settings.Kind == PromotionKind.ItemDiscount && r.Settings.ProductId == p.Id ||
                r.Settings.Kind == PromotionKind.CategoryDiscount && r.Settings.CategoryId.HasValue && p.Categories.Contains(r.Settings.CategoryId.Value))
                .Select(r => new { ProductId = p.Id, PromotionId = r.Id, r.Settings.Percent, r.Settings.MinimumSubtotal, r.Settings.Name })).ToList();
            return Results.Ok(discounts);
        }).RequireCors("PublicWebsiteCheckout").RequireRateLimiting("website-offers");
        app.MapGet("/api/website/promotions/{id:guid}/image", async (Guid id, AniseDbContext db, CancellationToken ct) =>
        {
            var item = await db.Promotions.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.ImageData, x.ImageContentType }).SingleOrDefaultAsync(ct);
            return item?.ImageData is null || item.ImageContentType is null ? Results.NotFound() : Results.File(item.ImageData, item.ImageContentType);
        }).RequireCors("PublicWebsiteMenu");
    }
}
public sealed record PromotionSettings(string Name, string? Description, bool Enabled, bool ShowOnOrder, bool ShowOnHome,
    PromotionKind Kind, PromotionAudience Audience, PromotionFulfilment Fulfilment, decimal MinimumSubtotal, decimal Percent,
    Guid? ProductId, Guid? CategoryId, decimal BundlePrice, List<BundleRequirement> BundleItems,
    DateTimeOffset? StartsAtUtc = null, DateTimeOffset? EndsAtUtc = null, List<Guid>? ChoiceProducts = null, int ChoiceQuantity = 0);

public sealed record PromotionDisplayRequest(bool IsReturningCustomer = false);

public sealed record ItemDiscountRequest(OrderType OrderType, bool IsReturningCustomer = false);

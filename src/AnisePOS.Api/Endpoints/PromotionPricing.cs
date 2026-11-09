using System.Text.Json;
using AnisePOS.Api.Data;
using AnisePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Endpoints;
public sealed record WebsiteOffer(decimal Discount, string? Name, bool CodeApplied, bool RecordCode, string? Error = null);
public static class PromotionPricing
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static async Task<WebsiteOffer> Best(AniseDbContext db, IConfiguration config, Order order, string? phone, string? promoCode, bool isReturningCustomer, CancellationToken ct)
    {
        var codeAmount = 0m; var recordCode = false;
        if (!string.IsNullOrWhiteSpace(promoCode))
        {
            var promo = await PromoCodes.Find(db, config, promoCode, ct);
            if (promo is null) return new(0,null,false,false,"This promo code is not valid or has expired.");
            if (order.Subtotal < promo.MinimumSubtotal) return new(0,null,false,false,$"This code requires a food subtotal of at least £{promo.MinimumSubtotal:F2}.");
            if (promo.OncePerPhone && (string.IsNullOrWhiteSpace(phone) || await PromoCodes.Used(db,promo.Code.ToUpperInvariant(),PromoCodes.NormalizePhone(phone),ct)))
                return new(0,null,false,false,"This promo code has already been used with this phone number.");
            codeAmount = decimal.Round(order.Subtotal * promo.Percent / 100m, 2, MidpointRounding.AwayFromZero); recordCode = promo.OncePerPhone;
        }
        var rows = await db.Promotions.AsNoTracking().Select(x => new { x.Id, x.SettingsJson }).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var rules = new List<PromotionRule>();
        foreach (var row in rows)
        {
            PromotionSettings? s; try { s = JsonSerializer.Deserialize<PromotionSettings>(row.SettingsJson, JsonOptions); } catch (JsonException) { continue; }
            if (s is null || !s.Enabled || s.StartsAtUtc > now || s.EndsAtUtc <= now || s.BundleItems is null) continue;
            rules.Add(new(row.Id,s.Name,s.Kind,s.Audience,s.Fulfilment,s.MinimumSubtotal,s.Percent,s.ProductId,s.CategoryId,s.BundlePrice,s.BundleItems,s.ChoiceProducts,s.ChoiceQuantity));
        }
        var existing = isReturningCustomer;
        var ids = order.Lines.Where(x => x.ProductId.HasValue).Select(x => x.ProductId!.Value).Distinct().ToList();
        var categories = await db.Products.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, Categories = x.Categories.Select(c => c.Id).ToList() }).ToDictionaryAsync(x => x.Id, x => x.Categories, ct);
        var items = order.Lines.Where(x => x.ProductId.HasValue).Select(x => new PromotionItem(x.ProductId!.Value,categories.GetValueOrDefault(x.ProductId.Value) ?? [], x.LineTotal / x.Quantity,x.Quantity)).ToList();
        var best = PromotionCalculator.Best(rules,items,order.DeliveryFee,order.OrderType,existing);
        if (best is not null && best.Total > codeAmount) return new(best.Total,best.Name,false,false);
        return codeAmount > 0 ? new(codeAmount,"Promo code: " + promoCode!.Trim().ToUpperInvariant(),true,recordCode) : new(0,null,false,false);
    }
}

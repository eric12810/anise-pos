namespace AnisePOS.Api.Domain;

// Pure pricing rules. A free item is discounted only when it is present in the basket.
public enum PromotionKind { ItemDiscount, CategoryDiscount, FreeItem, Bundle, FreeDelivery }
public enum PromotionAudience { All, New, Existing }
public enum PromotionFulfilment { Both, Collection, Delivery }
public sealed record PromotionItem(Guid ProductId, IReadOnlyCollection<Guid> CategoryIds, decimal UnitPrice, int Quantity);
public sealed record BundleRequirement(Guid ProductId, int Quantity);
public sealed record PromotionRule(Guid Id, string Name, PromotionKind Kind, PromotionAudience Audience,
    PromotionFulfilment Fulfilment, decimal MinimumSubtotal, decimal Percent, Guid? ProductId, Guid? CategoryId,
    decimal BundlePrice, IReadOnlyList<BundleRequirement> BundleItems, IReadOnlyList<Guid>? ChoiceProducts = null, int ChoiceQuantity = 0);
public sealed record PromotionDiscount(Guid Id, string Name, decimal FoodDiscount, decimal DeliveryDiscount)
{
    public decimal Total => FoodDiscount + DeliveryDiscount;
}
public static class PromotionCalculator
{
    // Rules are independently evaluated; the greatest saving wins and offers do not stack.
    public static PromotionDiscount? Best(IEnumerable<PromotionRule> rules, IReadOnlyList<PromotionItem> items,
        decimal deliveryFee, OrderType orderType, bool existingCustomer)
    {
        var subtotal = items.Sum(x => x.UnitPrice * x.Quantity);
        return rules.Select(rule => Evaluate(rule, items, subtotal, deliveryFee, orderType, existingCustomer))
            .Where(x => x is not null && x.Total > 0).OrderByDescending(x => x!.Total).ThenBy(x => x!.Id).FirstOrDefault();
    }
    public static PromotionDiscount? Evaluate(PromotionRule rule, IReadOnlyList<PromotionItem> items, decimal subtotal,
        decimal deliveryFee, OrderType orderType, bool existingCustomer)
    {
        if (subtotal < rule.MinimumSubtotal || rule.MinimumSubtotal < 0 || deliveryFee < 0 ||
            (rule.Audience == PromotionAudience.New && existingCustomer) || (rule.Audience == PromotionAudience.Existing && !existingCustomer) ||
            (rule.Fulfilment == PromotionFulfilment.Collection && orderType != OrderType.Collection) ||
            (rule.Fulfilment == PromotionFulfilment.Delivery && orderType != OrderType.Delivery)) return null;
        decimal food = 0, delivery = 0;
        switch (rule.Kind)
        {
            case PromotionKind.ItemDiscount when rule.ProductId.HasValue && rule.Percent is > 0 and <= 100:
                food = items.Where(x => x.ProductId == rule.ProductId).Sum(x => x.UnitPrice * x.Quantity) * rule.Percent / 100m; break;
            case PromotionKind.CategoryDiscount when rule.CategoryId.HasValue && rule.Percent is > 0 and <= 100:
                food = items.Where(x => x.CategoryIds.Contains(rule.CategoryId.Value)).Sum(x => x.UnitPrice * x.Quantity) * rule.Percent / 100m; break;
            case PromotionKind.FreeItem when rule.ProductId.HasValue:
                food = items.Where(x => x.ProductId == rule.ProductId && x.Quantity > 0).Select(x => x.UnitPrice).DefaultIfEmpty(0).Min(); break;
            case PromotionKind.FreeDelivery when orderType == OrderType.Delivery:
                delivery = deliveryFee; break;
            case PromotionKind.Bundle when (rule.BundleItems.Count > 0 || rule.ChoiceQuantity > 0) && rule.BundlePrice >= 0:
                if (rule.BundleItems.Any(x => x.Quantity <= 0) || rule.BundleItems.Select(x => x.ProductId).Distinct().Count() != rule.BundleItems.Count) return null;
                decimal normalPrice = 0;
                var units = items.SelectMany(x => Enumerable.Repeat((x.ProductId, x.UnitPrice), x.Quantity)).OrderByDescending(x => x.UnitPrice).ToList();
                foreach (var requirement in rule.BundleItems)
                {
                    for (var i = 0; i < requirement.Quantity; i++)
                    {
                        var index = units.FindIndex(x => x.ProductId == requirement.ProductId);
                        if (index < 0) return null;
                        normalPrice += units[index].UnitPrice; units.RemoveAt(index);
                    }
                }
                if (rule.ChoiceQuantity < 0 || (rule.ChoiceQuantity > 0 && (rule.ChoiceProducts is null || rule.ChoiceProducts.Count == 0))) return null;
                var choices = units.Where(x => rule.ChoiceProducts?.Contains(x.ProductId) == true).OrderByDescending(x => x.UnitPrice).Take(rule.ChoiceQuantity).ToList();
                if (choices.Count != rule.ChoiceQuantity) return null;
                normalPrice += choices.Sum(x => x.UnitPrice);
                food = Math.Max(0, normalPrice - rule.BundlePrice); break;
        }
        return new PromotionDiscount(rule.Id, rule.Name, decimal.Round(food, 2, MidpointRounding.AwayFromZero), delivery);
    }
}

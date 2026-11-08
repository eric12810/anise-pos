using AnisePOS.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
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

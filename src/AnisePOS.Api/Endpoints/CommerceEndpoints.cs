using AnisePOS.Api.Printing;
using AnisePOS.Api.Data;
using AnisePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace AnisePOS.Api.Endpoints;

public static class CommerceEndpoints
{
    public static bool ValidPosEstimate(OrderType type, int? minutes) => minutes is null ||
        (type is OrderType.Collection or OrderType.Delivery && minutes > 0 && minutes <= 1440 &&
         minutes % (type == OrderType.Collection ? 5 : 10) == 0);
    private static async Task<Dictionary<(Guid CategoryId, Guid ProductId), int>> ProductPositions(AniseDbContext db)
    {
        var links = await db.Set<Dictionary<string, object>>("ProductCategories").AsNoTracking()
            .Select(link => new { CategoryId = EF.Property<Guid>(link, "CategoryId"), ProductId = EF.Property<Guid>(link, "ProductId"), Order = EF.Property<int?>(link, "DisplayOrder") }).ToListAsync();
        var defaults = await db.Products.AsNoTracking().Select(p => new { p.Id, p.DisplayOrder }).ToDictionaryAsync(p => p.Id, p => p.DisplayOrder);
        return links.ToDictionary(x => (x.CategoryId, x.ProductId), x => x.Order ?? defaults[x.ProductId]);
    }

    public static void MapMenuEndpoints(this IEndpointRouteBuilder app)
    {
        var menu = app.MapGroup("/api/menu");
        var menuManagement = app.MapGroup("/api/menu").RequireAuthorization("ManageMenu");

        menu.MapGet("", async (AniseDbContext db) =>
        {
            var categories = await db.Categories.AsNoTracking()
                .Include(category => category.Products).ThenInclude(product => product.Variants)
                .Include(category => category.Products).ThenInclude(product => product.ModifierGroups).ThenInclude(link => link.ModifierGroup).ThenInclude(group => group.Modifiers)
                .Where(category => category.IsAvailable).OrderBy(category => category.DisplayOrder).ThenBy(category => category.Name).ToListAsync();
            var positions = await ProductPositions(db);
            var imageIds = (await db.ProductImages.AsNoTracking().Select(i => i.ProductId).ToListAsync()).ToHashSet();
            return categories.Select(category => new MenuCategoryResponse(category.Id, category.Name, category.Slug,
                category.Products.Where(product => product.IsAvailable).OrderBy(product => positions.GetValueOrDefault((category.Id, product.Id), product.DisplayOrder)).ThenBy(product => product.Name)
                    .Select(product => new MenuProductResponse(product.Id, product.Name, product.Slug, product.Description, product.IsVegan, product.VeganSection, product.ChineseName, imageIds.Contains(product.Id) ? $"/api/menu/products/{product.Id}/image" : null,
                        product.Variants.Where(variant => variant.IsAvailable).OrderBy(variant => variant.DisplayOrder).ThenBy(variant => variant.Name)
                            .Select(variant => new MenuVariantResponse(variant.Id, variant.Name, variant.Sku, variant.Price)).ToList(),
                        product.ModifierGroups.OrderBy(link => link.DisplayOrder).Select(link => new MenuModifierGroupResponse(link.ModifierGroup.Id, link.ModifierGroup.Name,
                            link.ModifierGroup.MinimumSelections, link.ModifierGroup.MaximumSelections,
                            link.ModifierGroup.Modifiers.Where(modifier => modifier.IsAvailable).OrderBy(modifier => modifier.DisplayOrder).ThenBy(modifier => modifier.Name)
                                .Select(modifier => new MenuModifierResponse(modifier.Id, modifier.Name, modifier.PriceAdjustment)).ToList())).ToList())).ToList())).ToList();
        }).RequireCors("PublicWebsiteMenu");

        menu.MapGet("/products/{productId:guid}/image", async (Guid productId, AniseDbContext db) =>
        {
            var image = await db.ProductImages.AsNoTracking().SingleOrDefaultAsync(x => x.ProductId == productId);
            return image is null ? Results.NotFound() : Results.File(image.Data, image.ContentType);
        }).RequireCors("PublicWebsiteMenu");

        menuManagement.MapPost("/products/{productId:guid}/image", async (Guid productId, HttpRequest request, AniseDbContext db) =>
        {
            const int maxBytes = 5 * 1024 * 1024;
            if (request.ContentLength is > maxBytes) return Invalid("image", "Image must be 5 MB or smaller.");
            if (!await db.Products.AnyAsync(p => p.Id == productId)) return Results.NotFound();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > maxBytes) return Invalid("image", "Image must be 5 MB or smaller.");
                buffer.Write(chunk, 0, read);
            }
            var bytes = buffer.ToArray();
            var type = bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff ? "image/jpeg"
                : bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
                : bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8) ? "image/webp" : null;
            if (type is null) return Invalid("image", "Choose a JPEG, PNG or WebP image.");
            var image = await db.ProductImages.FindAsync(productId);
            if (image is null) { image = new ProductImage { ProductId = productId }; db.ProductImages.Add(image); }
            image.Data = bytes; image.ContentType = type;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
        menuManagement.MapDelete("/products/{productId:guid}/image", async (Guid productId, AniseDbContext db) =>
        {
            var image = await db.ProductImages.FindAsync(productId);
            if (image is null) return Results.NotFound();
            db.ProductImages.Remove(image); await db.SaveChangesAsync(); return Results.NoContent();
        });

        menuManagement.MapPost("/categories", async (CreateCategoryRequest request, AniseDbContext db) =>
        {
            var slug = Slugify(request.Slug ?? request.Name);
            if (string.IsNullOrWhiteSpace(request.Name) || await db.Categories.AnyAsync(x => x.Slug == slug))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["A unique category name is required."] });
            var category = new Category { Name = request.Name.Trim(), Slug = slug, DisplayOrder = request.DisplayOrder };
            db.Categories.Add(category);
            await db.SaveChangesAsync();
            return Results.Created($"/api/menu/categories/{category.Id}", new { category.Id });
        });

        menuManagement.MapPost("/products", async (CreateProductRequest request, AniseDbContext db) =>
        {
            var slug = Slugify(request.Slug ?? request.Name);
            var categoryIds = request.CategoryIds ?? (request.CategoryId != Guid.Empty ? [request.CategoryId] : []);
            var categories = await db.Categories.Where(c => categoryIds.Contains(c.Id)).ToListAsync();
            if (string.IsNullOrWhiteSpace(request.Name) || request.Variants.Count == 0 || categoryIds.Count == 0 || categories.Count != categoryIds.Distinct().Count() || await db.Products.AnyAsync(x => x.Slug == slug))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["product"] = ["A unique name, valid category, and at least one variant are required."] });
            if (request.Variants.Any(x => string.IsNullOrWhiteSpace(x.Sku) || x.Price < 0) || request.Variants.Select(x => x.Sku).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Variants.Count)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["variants"] = ["Variants need distinct SKUs and non-negative prices."] });

            if (request.ChineseName?.Trim().Length > 150) return Invalid("chineseName", "Chinese name must be 150 characters or fewer.");
            var product = new Product { CategoryId = categories[0].Id, Categories = categories, ChineseName = string.IsNullOrWhiteSpace(request.ChineseName) ? null : request.ChineseName.Trim(), Name = request.Name.Trim(), Slug = slug, Description = request.Description?.Trim(), IsVegan = request.IsVegan, DisplayOrder = request.DisplayOrder };
            product.Variants = request.Variants.Select((variant, index) => new ProductVariant { Name = variant.Name.Trim(), Sku = variant.Sku.Trim(), Price = variant.Price, DisplayOrder = index }).ToList();
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return Results.Created($"/api/menu/products/{product.Id}", new { product.Id });
        });

        menuManagement.MapPost("/modifier-groups", async (CreateModifierGroupRequest request, AniseDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || request.MinimumSelections < 0 || request.MaximumSelections < request.MinimumSelections || request.Modifiers.Count == 0 || request.Modifiers.Any(x => string.IsNullOrWhiteSpace(x.Name)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["modifierGroup"] = ["A name, valid selection limits, and at least one modifier are required."] });
            var group = new ModifierGroup { Name = request.Name.Trim(), MinimumSelections = request.MinimumSelections, MaximumSelections = request.MaximumSelections };
            group.Modifiers = request.Modifiers.Select((modifier, index) => new Modifier { Name = modifier.Name.Trim(), PriceAdjustment = modifier.PriceAdjustment, DisplayOrder = index }).ToList();
            db.ModifierGroups.Add(group);
            await db.SaveChangesAsync();
            return Results.Created($"/api/menu/modifier-groups/{group.Id}", new { group.Id });
        });

        menuManagement.MapGet("/modifier-groups", async (AniseDbContext db) => await db.ModifierGroups.AsNoTracking().OrderBy(x => x.Name)
            .Select(group => new { group.Id, group.Name, group.MinimumSelections, group.MaximumSelections, Modifiers = group.Modifiers.OrderBy(x => x.DisplayOrder).Select(modifier => new { modifier.Id, modifier.Name, modifier.PriceAdjustment, modifier.IsAvailable }).ToList() }).ToListAsync());

        menuManagement.MapGet("/admin", async (AniseDbContext db) =>
        {
            var positions = await ProductPositions(db);
            var categories = await db.Categories.AsNoTracking().OrderBy(c => c.DisplayOrder).ThenBy(c => c.Name)
                .Select(c => new AdminCategoryResponse(c.Id, c.Name, c.IsAvailable,
                    c.Products.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name)
                        .Select(p => new AdminProductResponse(p.Id, p.Name, p.Description, p.IsAvailable, p.IsVegan, p.ChineseName, db.ProductImages.Any(i => i.ProductId == p.Id) ? "/api/menu/products/" + p.Id + "/image" : null,
                            p.Variants.OrderBy(v => v.DisplayOrder).ThenBy(v => v.Name)
                                .Select(v => new AdminVariantResponse(v.Id, v.Name, v.Sku, v.Price, v.IsAvailable)).ToList(),
                            p.ModifierGroups.OrderBy(link => link.DisplayOrder)
                                .Select(link => link.ModifierGroup.Name).ToList())).ToList())).ToListAsync();
            // Preserve the original product ordering for memberships that have not been reordered.
            foreach (var category in categories)
                category.Products.Sort((a, b) => {
                    var ao = positions[(category.Id, a.Id)];
                    var bo = positions[(category.Id, b.Id)];
                    var comparison = ao.CompareTo(bo);
                    return comparison != 0 ? comparison : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                });
            return categories;
        });

        menuManagement.MapPut("/categories/order", async (ReorderRequest request, AniseDbContext db) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            var categories = await db.Categories.ToListAsync();
            if (request.Ids is null || request.Ids.Count != categories.Count || request.Ids.Distinct().Count() != categories.Count || categories.Any(c => !request.Ids.Contains(c.Id)))
                return Results.Conflict(new { detail = "The categories changed. Refresh and try again." });
            var positions = request.Ids.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
            foreach (var category in categories) category.DisplayOrder = positions[category.Id];
            await db.SaveChangesAsync(); await transaction.CommitAsync(); return Results.NoContent();
        });
        menuManagement.MapPut("/categories/{categoryId:guid}/products/order", async (Guid categoryId, ReorderRequest request, AniseDbContext db) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            if (!await db.Categories.AnyAsync(c => c.Id == categoryId)) return Results.NotFound();
            var links = await db.Set<Dictionary<string, object>>("ProductCategories")
                .Where(link => EF.Property<Guid>(link, "CategoryId") == categoryId).ToListAsync();
            if (request.Ids is null || request.Ids.Count != links.Count || request.Ids.Distinct().Count() != links.Count || links.Any(link => !request.Ids.Contains((Guid)link["ProductId"])))
                return Results.Conflict(new { detail = "The dishes in this category changed. Refresh and try again." });
            var positions = request.Ids.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
            foreach (var link in links) link["DisplayOrder"] = positions[(Guid)link["ProductId"]];
            await db.SaveChangesAsync(); await transaction.CommitAsync(); return Results.NoContent();
        });

        menuManagement.MapDelete("/categories/{categoryId:guid}", async (Guid categoryId, AniseDbContext db) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            var category = await db.Categories.Include(c => c.Products).SingleOrDefaultAsync(c => c.Id == categoryId);
            if (category is null) return Results.NotFound();
            if (category.Products.Count != 0 || await db.Products.AnyAsync(p => p.CategoryId == categoryId))
                return Results.Conflict(new { detail = "Move or remove every dish from this category before deleting it." });
            db.Categories.Remove(category);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        });

        menuManagement.MapDelete("/products/{productId:guid}", async (Guid productId, AniseDbContext db) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
            var product = await db.Products.Include(p => p.Categories).Include(p => p.Variants)
                .Include(p => p.ModifierGroups).SingleOrDefaultAsync(p => p.Id == productId);
            if (product is null) return Results.NotFound();
            // Order lines keep their own names and prices; clear references before removing catalog records.
            var variantIds = product.Variants.Select(v => v.Id).ToList();
            await db.OrderLines.Where(l => l.ProductId == productId || (l.ProductVariantId.HasValue && variantIds.Contains(l.ProductVariantId.Value)))
                .ExecuteUpdateAsync(setters => setters.SetProperty(l => l.ProductId, (Guid?)null).SetProperty(l => l.ProductVariantId, (Guid?)null));
            product.Categories.Clear();
            db.ProductModifierGroups.RemoveRange(product.ModifierGroups);
            db.ProductVariants.RemoveRange(product.Variants);
            db.Products.Remove(product);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.NoContent();
        });

        menuManagement.MapPut("/categories/{categoryId:guid}", async (Guid categoryId, UpdateCategoryRequest request, AniseDbContext db) =>
        {
            var category = await db.Categories.FindAsync(categoryId);
            if (category is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name)) return Invalid("name", "A category name is required.");
            category.Name = request.Name.Trim(); category.IsAvailable = request.IsAvailable; category.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(); return Results.NoContent();
        });

        menuManagement.MapPut("/products/{productId:guid}", async (Guid productId, UpdateProductRequest request, AniseDbContext db) =>
        {
            var product = await db.Products.Include(p => p.Categories).SingleOrDefaultAsync(p => p.Id == productId);
            if (product is null) return Results.NotFound();
            var categoryIds = request.CategoryIds ?? (request.CategoryId != Guid.Empty ? [request.CategoryId] : []);
            var categories = await db.Categories.Where(c => categoryIds.Contains(c.Id)).ToListAsync();
            if (string.IsNullOrWhiteSpace(request.Name) || categoryIds.Count == 0 || categories.Count != categoryIds.Distinct().Count())
                return Invalid("product", "A product name and at least one valid category are required.");
            if (request.ChineseName.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String)) return Invalid("chineseName", "Chinese name must be text.");
            var chineseName = request.ChineseName.ValueKind == JsonValueKind.String ? request.ChineseName.GetString()?.Trim() : null;
            if (chineseName?.Length > 150) return Invalid("chineseName", "Chinese name must be 150 characters or fewer.");
            if (request.ChineseName.ValueKind != JsonValueKind.Undefined) product.ChineseName = string.IsNullOrWhiteSpace(chineseName) ? null : chineseName;
            product.Categories.Clear(); foreach (var category in categories) product.Categories.Add(category);
            product.CategoryId = categories[0].Id; product.Name = request.Name.Trim(); product.Description = request.Description?.Trim();
            product.IsAvailable = request.IsAvailable; product.IsVegan = request.IsVegan ?? product.IsVegan; product.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(); return Results.NoContent();
        });

        menuManagement.MapPost("/products/{productId:guid}/variants", async (Guid productId, CreateVariantRequest request, AniseDbContext db) =>
        {
            if (!await db.Products.AnyAsync(p => p.Id == productId)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Sku) || request.Price < 0 || await db.ProductVariants.AnyAsync(v => v.Sku == request.Sku.Trim()))
                return Invalid("variant", "A name, unique SKU, and non-negative price are required.");
            var displayOrder = await db.ProductVariants.Where(v => v.ProductId == productId).CountAsync();
            var variant = new ProductVariant { ProductId = productId, Name = request.Name.Trim(), Sku = request.Sku.Trim(), Price = request.Price, DisplayOrder = displayOrder };
            db.ProductVariants.Add(variant); await db.SaveChangesAsync(); return Results.Created($"/api/menu/variants/{variant.Id}", new { variant.Id });
        });

        menuManagement.MapPut("/variants/{variantId:guid}", async (Guid variantId, UpdateVariantRequest request, AniseDbContext db) =>
        {
            var variant = await db.ProductVariants.FindAsync(variantId);
            if (variant is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name) || request.Price < 0) return Invalid("variant", "A name and non-negative price are required.");
            variant.Name = request.Name.Trim(); variant.Price = request.Price; variant.IsAvailable = request.IsAvailable; variant.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(); return Results.NoContent();
        });

        menuManagement.MapPut("/modifier-groups/{groupId:guid}", async (Guid groupId, UpdateModifierGroupRequest request, AniseDbContext db) =>
        {
            var group = await db.ModifierGroups.FindAsync(groupId);
            if (group is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name) || request.MinimumSelections < 0 || request.MaximumSelections < request.MinimumSelections)
                return Invalid("modifierGroup", "A name and valid choice limits are required.");
            group.Name = request.Name.Trim(); group.MinimumSelections = request.MinimumSelections; group.MaximumSelections = request.MaximumSelections; group.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(); return Results.NoContent();
        });

        menuManagement.MapPost("/modifier-groups/{groupId:guid}/modifiers", async (Guid groupId, CreateModifierRequest request, AniseDbContext db) =>
        {
            if (!await db.ModifierGroups.AnyAsync(g => g.Id == groupId)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name)) return Invalid("modifier", "A choice name is required.");
            var displayOrder = await db.Modifiers.Where(m => m.ModifierGroupId == groupId).CountAsync();
            var modifier = new Modifier { ModifierGroupId = groupId, Name = request.Name.Trim(), PriceAdjustment = request.PriceAdjustment, DisplayOrder = displayOrder };
            db.Modifiers.Add(modifier); await db.SaveChangesAsync(); return Results.Created($"/api/menu/modifiers/{modifier.Id}", new { modifier.Id });
        });

        menuManagement.MapPut("/modifiers/{modifierId:guid}", async (Guid modifierId, UpdateModifierRequest request, AniseDbContext db) =>
        {
            var modifier = await db.Modifiers.FindAsync(modifierId);
            if (modifier is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Name)) return Invalid("modifier", "A choice name is required.");
            modifier.Name = request.Name.Trim(); modifier.PriceAdjustment = request.PriceAdjustment; modifier.IsAvailable = request.IsAvailable; modifier.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(); return Results.NoContent();
        });

        menuManagement.MapPost("/import", async (MenuImportRequest request, AniseDbContext db) =>
        {
            var addedCategories = 0; var addedProducts = 0; var updatedProducts = 0;
            foreach (var sourceCategory in request.Categories)
            {
                var categorySlug = Slugify(sourceCategory.Name);
                var category = db.Categories.Local.FirstOrDefault(x => x.Slug == categorySlug) ?? await db.Categories.SingleOrDefaultAsync(x => x.Slug == categorySlug);
                if (category is null) { category = new Category { Name = sourceCategory.Name.Trim(), Slug = categorySlug, DisplayOrder = addedCategories++ }; db.Categories.Add(category); }
                foreach (var sourceProduct in sourceCategory.Products)
                {
                    var slug = Slugify(sourceProduct.Name);
                    var product = db.Products.Local.FirstOrDefault(x => x.Slug == slug) ?? await db.Products.Include(x => x.Variants).Include(x => x.Categories).SingleOrDefaultAsync(x => x.Slug == slug);
                    if (product is null)
                    {
                        product = new Product { CategoryId = category.Id, Categories = new List<Category> { category }, Name = sourceProduct.Name.Trim(), Slug = slug, Description = sourceProduct.Description?.Trim(), DisplayOrder = addedProducts };
                        product.Variants.Add(new ProductVariant { Name = "Regular", Sku = $"FB-{slug}"[..Math.Min(80, $"FB-{slug}".Length)], Price = sourceProduct.Price });
                        db.Products.Add(product); addedProducts++;
                    }
                    else { if (!product.Categories.Any(c => c.Id == category.Id)) product.Categories.Add(category); product.Description = sourceProduct.Description?.Trim(); product.Variants.First().Price = sourceProduct.Price; product.UpdatedAtUtc = DateTimeOffset.UtcNow; updatedProducts++; }
                }
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { addedCategories, addedProducts, updatedProducts });
        });

        menuManagement.MapPut("/products/{productId:guid}/modifier-groups/{groupId:guid}", async (Guid productId, Guid groupId, AniseDbContext db) =>
        {
            if (!await db.Products.AnyAsync(x => x.Id == productId) || !await db.ModifierGroups.AnyAsync(x => x.Id == groupId)) return Results.NotFound();
            if (!await db.ProductModifierGroups.AnyAsync(x => x.ProductId == productId && x.ModifierGroupId == groupId))
            {
                db.ProductModifierGroups.Add(new ProductModifierGroup { ProductId = productId, ModifierGroupId = groupId });
                await db.SaveChangesAsync();
            }
            return Results.NoContent();
        });
    }

    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/orders").RequireAuthorization();

        orders.MapGet("/history", async (string? query, OrderStatus? status, OrderType? orderType,
            DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize, AniseDbContext db) =>
        {
            page = Math.Max(page, 1);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var ordersQuery = db.Orders.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(query))
            {
                var term = query.Trim();
                if (long.TryParse(term.TrimStart('#'), out var orderNumber))
                    ordersQuery = ordersQuery.Where(o => o.OrderNumber == orderNumber ||
                        (o.CustomerPhone != null && o.CustomerPhone.Contains(term)));
                else
                    ordersQuery = ordersQuery.Where(o =>
                        (o.CustomerName != null && o.CustomerName.Contains(term)) ||
                        (o.CustomerPhone != null && o.CustomerPhone.Contains(term)) ||
                        (o.DeliveryAddress != null && o.DeliveryAddress.Contains(term)));
            }
            if (status.HasValue) ordersQuery = ordersQuery.Where(o => o.Status == status.Value);
            if (orderType.HasValue) ordersQuery = ordersQuery.Where(o => o.OrderType == orderType.Value);
            if (from.HasValue) ordersQuery = ordersQuery.Where(o => o.CreatedAtUtc >= from.Value);
            if (to.HasValue) ordersQuery = ordersQuery.Where(o => o.CreatedAtUtc < to.Value);

            var totalCount = await ordersQuery.CountAsync();
            var items = await ordersQuery.OrderByDescending(o => o.CreatedAtUtc).ThenByDescending(o => o.OrderNumber)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(o => new OrderHistoryItemResponse(o.OrderNumber, o.CreatedAtUtc, o.OrderType, o.Status,
                    o.CustomerName, o.CustomerPhone, o.Total, o.Lines.Sum(l => l.Quantity),
                    o.Payments.OrderByDescending(p => p.CreatedAtUtc)
                        .Select(p => new ReceiptPaymentResponse(p.Method, p.Status)).FirstOrDefault()))
                .ToListAsync();
            return Results.Ok(new OrderHistoryResponse(items, page, pageSize, totalCount));
        });

        orders.MapPost("", async (CreateOrderRequest request, AniseDbContext db, ClaimsPrincipal staff) =>
        {
            if (request.Lines.Count == 0 || request.Lines.Any(line => line.Quantity < 1))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["lines"] = ["An order needs at least one line with a positive quantity."] });
            if (request.PaymentMethod is not PaymentMethod.Cash and not PaymentMethod.Card)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["paymentMethod"] = ["Choose Cash or Card."] });
            if (!ValidPosEstimate(request.OrderType, request.EstimatedMinutes))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["estimatedMinutes"] = ["Choose a valid estimate in 5-minute collection or 10-minute delivery increments."] });

            var variantIds = request.Lines.Select(line => line.VariantId).Distinct().ToList();
            var variants = await db.ProductVariants.Include(variant => variant.Product).ThenInclude(product => product.ModifierGroups).ThenInclude(link => link.ModifierGroup).ThenInclude(group => group.Modifiers)
                .Where(variant => variantIds.Contains(variant.Id) && variant.IsAvailable && variant.Product.IsAvailable && variant.Product.Categories.Any(category => category.IsAvailable)).ToDictionaryAsync(variant => variant.Id);
            if (variants.Count != variantIds.Count) return Results.ValidationProblem(new Dictionary<string, string[]> { ["lines"] = ["One or more selected variants are unavailable."] });

            var order = new Order { CustomerId = request.CustomerId, OrderType = request.OrderType, CustomerName = request.CustomerName?.Trim(), CustomerPhone = request.CustomerPhone?.Trim(), Notes = request.Notes?.Trim(), DeliveryAddress = request.DeliveryAddress?.Trim() };
            if (request.EstimatedMinutes is int estimate) order.EstimatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(estimate);
            foreach (var requestLine in request.Lines)
            {
                var variant = variants[requestLine.VariantId];
                var permittedGroups = variant.Product.ModifierGroups.ToDictionary(link => link.ModifierGroupId, link => link.ModifierGroup);
                var selectedModifiers = requestLine.ModifierIds.Distinct().ToList();
                var modifiers = permittedGroups.Values.SelectMany(group => group.Modifiers).Where(modifier => selectedModifiers.Contains(modifier.Id) && modifier.IsAvailable).ToList();
                if (modifiers.Count != selectedModifiers.Count || modifiers.Any(modifier => !permittedGroups.ContainsKey(modifier.ModifierGroupId)))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["modifiers"] = ["One or more modifiers are unavailable or do not belong to this product."] });
                if (permittedGroups.Values.Any(group => { var count = modifiers.Count(modifier => modifier.ModifierGroupId == group.Id); return count < group.MinimumSelections || count > group.MaximumSelections; }))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["modifiers"] = ["Modifier selections do not meet the product's requirements."] });

                var modifierTotal = modifiers.Sum(modifier => modifier.PriceAdjustment);
                var line = new OrderLine { ProductId = variant.ProductId, ProductVariantId = variant.Id, ProductName = variant.Product.Name, VariantName = variant.Name, UnitPrice = variant.Price, Quantity = requestLine.Quantity, LineTotal = (variant.Price + modifierTotal) * requestLine.Quantity };
                line.Modifiers = modifiers.Select(modifier => new OrderLineModifier { ModifierId = modifier.Id, ModifierName = modifier.Name, PriceAdjustment = modifier.PriceAdjustment }).ToList();
                order.Lines.Add(line);
            }
            order.Subtotal = order.Lines.Sum(line => line.LineTotal);
            order.DeliveryFee = request.DeliveryFee < 0 ? 0 : request.DeliveryFee;
            order.CustomFee = request.CustomFee < 0 ? 0 : request.CustomFee;
            order.CustomFeeDescription = order.CustomFee > 0
                ? string.IsNullOrWhiteSpace(request.CustomFeeDescription) ? "Additional charge" : request.CustomFeeDescription.Trim()
                : null;
            order.Total = order.Subtotal + order.DeliveryFee + order.CustomFee;
            order.Payments.Add(new Payment { Method = request.PaymentMethod, Status = PaymentStatus.Pending, Amount = order.Total });
            order.StatusHistory.Add(new OrderStatusHistory { Status = OrderStatus.Draft,
                Note = string.IsNullOrWhiteSpace(request.Source) ? "Order created" : $"Order created via {request.Source.Trim()}",
                ChangedBy = staff.FindFirstValue(ClaimTypes.NameIdentifier) });
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            return Results.Created($"/api/orders/{order.OrderNumber}", new CreateOrderResponse(order.OrderNumber, order.Status, order.Total));
        });

        orders.MapGet("/address-by-phone", async (string phone, AniseDbContext db) =>
        {
            var compactPhone = phone.Trim().Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");
            if (compactPhone.Length < 7) return Results.NotFound();
            var match = await db.Orders.AsNoTracking()
                .Where(o => o.OrderType == OrderType.Delivery && o.CustomerPhone != null && o.DeliveryAddress != null && o.DeliveryAddress != ""
                    && o.CustomerPhone.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "") == compactPhone)
                .OrderByDescending(o => o.CreatedAtUtc)
                .Select(o => new { o.CustomerName, o.DeliveryAddress })
                .FirstOrDefaultAsync();
            return match is null ? Results.NotFound() : Results.Ok(match);
        });

        orders.MapGet("/addresses-by-postcode", async (string postcode, IConfiguration configuration,
            IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            var compactPostcode = new string(postcode.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (compactPostcode.Length is < 5 or > 8)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["postcode"] = ["Enter a valid UK postcode."] });
            var apiKey = configuration["GoAddress:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
                return Results.Problem("Address lookup is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);

            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://portal.goaddress.io/api/address/{Uri.EscapeDataString(compactPostcode)}");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            try
            {
                var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(30);
                using var response = await client.SendAsync(request, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return Results.Ok(Array.Empty<PostcodeAddressResponse>());
                if (!response.IsSuccessStatusCode)
                {
                    var detail = response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "GoAddress rejected the configured API key.",
                        System.Net.HttpStatusCode.PaymentRequired => "The GoAddress account has no remaining lookup allowance.",
                        System.Net.HttpStatusCode.TooManyRequests => "GoAddress's lookup rate limit has been reached. Try again shortly.",
                        _ => $"GoAddress could not complete the lookup (provider status {(int)response.StatusCode})."
                    };
                    return Results.Problem(detail, statusCode: StatusCodes.Status502BadGateway);
                }
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = json.RootElement;
                double? distanceMiles = null;
                try
                {
                    var shop = await LookupPostcodeCoordinatesAsync(client, "NW18ET", ct);
                    var customer = await LookupPostcodeCoordinatesAsync(client, compactPostcode, ct);
                    if (shop is not null && customer is not null)
                        distanceMiles = Math.Round(CalculateDistanceMiles(shop.Value, customer.Value), 1);
                }
                catch (HttpRequestException) { }
                catch (JsonException) { }
                var result = new List<PostcodeAddressResponse>();
                var fallbackPostcode = root.TryGetProperty("postcode", out var rootPostcode)
                    ? rootPostcode.GetString() ?? postcode.Trim().ToUpperInvariant()
                    : postcode.Trim().ToUpperInvariant();
                if (root.TryGetProperty("new_address_res", out var structured) && structured.ValueKind == JsonValueKind.Array)
                {
                    result.AddRange(structured.EnumerateArray().Select(address => new PostcodeAddressResponse(
                        address.TryGetProperty("raw_address", out var rawAddress) ? rawAddress.GetString() ?? "" : "",
                        address.TryGetProperty("postcode", out var returnedPostcode) ? returnedPostcode.GetString() ?? fallbackPostcode : fallbackPostcode,
                        address.TryGetProperty("addressid", out var addressId) ? addressId.GetString() : null,
                        distanceMiles)));
                }
                else if (root.TryGetProperty("results", out var addresses) && addresses.ValueKind == JsonValueKind.Array)
                {
                    result.AddRange(addresses.EnumerateArray().Select(address => new PostcodeAddressResponse(
                        address.TryGetProperty("address", out var formatted) ? formatted.GetString() ?? "" : "",
                        address.TryGetProperty("postcode", out var returnedPostcode) ? returnedPostcode.GetString() ?? fallbackPostcode : fallbackPostcode,
                        address.TryGetProperty("addressid", out var addressId) ? addressId.GetString() : null,
                        distanceMiles)));
                }
                else if (root.TryGetProperty("addresses", out var labels) && labels.ValueKind == JsonValueKind.Array)
                {
                    result.AddRange(labels.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(address => new PostcodeAddressResponse(address.GetString() ?? "", fallbackPostcode, null, distanceMiles)));
                }
                result = result.Where(address => !string.IsNullOrWhiteSpace(address.Address)).Take(200).ToList();
                return Results.Ok(result);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Problem("The address lookup timed out.", statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (HttpRequestException)
            {
                return Results.Problem("The address provider could not be reached.", statusCode: StatusCodes.Status502BadGateway);
            }
        });

        orders.MapPost("/{orderNumber:long}/print/{type}", async (long orderNumber, string type,
            AniseDbContext db, ReceiptPrinter printer, ILogger<ReceiptPrinter> logger, CancellationToken ct) =>
        {
            if (type is not ("customer" or "kitchen")) return Results.BadRequest(new { message = "Invalid receipt type." });
            var order = await db.Orders.AsNoTracking().Include(o => o.Payments)
                .Include(o => o.Lines).ThenInclude(l => l.Modifiers)
                .SingleOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);
            if (order is null) return Results.NotFound();
            try
            {
                var printerHost = await db.DeliverySettings.AsNoTracking().Where(x => x.Id == 1).Select(x => x.PrinterIpAddress).SingleOrDefaultAsync(ct);
                await printer.PrintAsync(order, type == "kitchen", ct, printerHost);
                return Results.Ok(new { message = "Sent to printer. Check the printer for the receipt." });
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not send order {OrderNumber} to receipt printer", orderNumber);
                return Results.Problem("Could not send to the printer. Check that the POS server can reach it. If printing started, check the printer before retrying.", statusCode: 503);
            }
        });

        orders.MapGet("/{orderNumber:long}/receipt", async (long orderNumber, AniseDbContext db) =>
        {
            var receipt = await db.Orders.AsNoTracking().Where(o => o.OrderNumber == orderNumber)
                .Select(o => new ReceiptResponse(o.OrderNumber, o.OrderType, o.Status, o.CreatedAtUtc,
                    o.CustomerName, o.CustomerPhone, o.DeliveryAddress, o.Notes, o.EstimatedAtUtc, o.Subtotal,
                    o.DiscountTotal, o.DeliveryFee, o.CustomFeeDescription, o.CustomFee, o.Total,
                    o.Payments.OrderByDescending(p => p.CreatedAtUtc)
                        .Select(p => new ReceiptPaymentResponse(p.Method, p.Status)).FirstOrDefault(),
                    o.Lines.OrderBy(l => l.CreatedAtUtc).ThenBy(l => l.Id)
                        .Select(l => new ReceiptLineResponse(l.ProductName, db.Products.Where(p => p.Id == l.ProductId).Select(p => p.ChineseName).FirstOrDefault(), l.VariantName, l.Quantity,
                            l.UnitPrice, l.LineTotal, l.Notes, l.Modifiers.OrderBy(m => m.CreatedAtUtc)
                                .Select(m => new ReceiptModifierResponse(m.ModifierName, m.PriceAdjustment)).ToList()))
                        .ToList()))
                .SingleOrDefaultAsync();
            return receipt is null ? Results.NotFound() : Results.Ok(receipt);
        });

        orders.MapGet("/{orderNumber:long}", async (long orderNumber, AniseDbContext db) =>
        {
            var order = await db.Orders.AsNoTracking().Include(x => x.Lines).ThenInclude(x => x.Modifiers).Include(x => x.Payments).Include(x => x.StatusHistory)
                .SingleOrDefaultAsync(x => x.OrderNumber == orderNumber);
            return order is null ? Results.NotFound() : Results.Ok(order);
        });
    }

    private static async Task<(double Latitude, double Longitude)?> LookupPostcodeCoordinatesAsync(
        HttpClient client, string postcode, CancellationToken ct)
    {
        using var response = await client.GetAsync(
            $"https://api.postcodes.io/postcodes/{Uri.EscapeDataString(postcode)}", ct);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!json.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("latitude", out var latitude) || latitude.ValueKind != JsonValueKind.Number ||
            !result.TryGetProperty("longitude", out var longitude) || longitude.ValueKind != JsonValueKind.Number)
            return null;
        return (latitude.GetDouble(), longitude.GetDouble());
    }

    private static double CalculateDistanceMiles(
        (double Latitude, double Longitude) from, (double Latitude, double Longitude) to)
    {
        const double earthRadiusMiles = 3958.7613;
        static double Radians(double degrees) => degrees * Math.PI / 180d;
        var latitudeDelta = Radians(to.Latitude - from.Latitude);
        var longitudeDelta = Radians(to.Longitude - from.Longitude);
        var a = Math.Pow(Math.Sin(latitudeDelta / 2), 2) +
            Math.Cos(Radians(from.Latitude)) * Math.Cos(Radians(to.Latitude)) *
            Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return earthRadiusMiles * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static string Slugify(string input) => string.Join('-', input.Trim().ToLowerInvariant().Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries));
    private static IResult Invalid(string key, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [message] });
}

public sealed record CreateCategoryRequest(string Name, string? Slug, int DisplayOrder = 0);
public sealed record CreateProductRequest(Guid CategoryId, string Name, string? Slug, string? Description, int DisplayOrder, List<CreateVariantRequest> Variants, bool IsVegan = false, List<Guid>? CategoryIds = null, string? ChineseName = null);
public sealed record CreateVariantRequest(string Name, string Sku, decimal Price);
public sealed record CreateModifierGroupRequest(string Name, int MinimumSelections, int MaximumSelections, List<CreateModifierRequest> Modifiers);
public sealed record CreateModifierRequest(string Name, decimal PriceAdjustment);
public sealed record UpdateCategoryRequest(string Name, bool IsAvailable);
public sealed record UpdateProductRequest(Guid CategoryId, string Name, string? Description, bool IsAvailable, bool? IsVegan = null, List<Guid>? CategoryIds = null, JsonElement ChineseName = default);
public sealed record UpdateVariantRequest(string Name, decimal Price, bool IsAvailable);
public sealed record UpdateModifierGroupRequest(string Name, int MinimumSelections, int MaximumSelections);
public sealed record UpdateModifierRequest(string Name, decimal PriceAdjustment, bool IsAvailable);
public sealed record CreateOrderRequest(Guid? CustomerId, OrderType OrderType, PaymentMethod PaymentMethod, string? CustomerName, string? CustomerPhone, string? Notes, string? DeliveryAddress, decimal DeliveryFee, string? CustomFeeDescription, decimal CustomFee, string? Source, List<CreateOrderLineRequest> Lines, int? EstimatedMinutes = null);
public sealed record CreateOrderLineRequest(Guid VariantId, int Quantity, List<Guid> ModifierIds);
public sealed record MenuCategoryResponse(Guid Id, string Name, string Slug, List<MenuProductResponse> Products);
public sealed record MenuProductResponse(Guid Id, string Name, string Slug, string? Description, bool IsVegan, string? VeganSection, string? ChineseName, string? ImageUrl, List<MenuVariantResponse> Variants, List<MenuModifierGroupResponse> ModifierGroups);
public sealed record MenuVariantResponse(Guid Id, string Name, string Sku, decimal Price);
public sealed record MenuModifierGroupResponse(Guid Id, string Name, int MinimumSelections, int MaximumSelections, List<MenuModifierResponse> Modifiers);
public sealed record MenuModifierResponse(Guid Id, string Name, decimal PriceAdjustment);
public sealed record AdminCategoryResponse(Guid Id, string Name, bool IsAvailable, List<AdminProductResponse> Products);
public sealed record AdminProductResponse(Guid Id, string Name, string? Description, bool IsAvailable, bool IsVegan, string? ChineseName, string? ImageUrl,
    List<AdminVariantResponse> Variants, List<string> ModifierGroups);
public sealed record AdminVariantResponse(Guid Id, string Name, string Sku, decimal Price, bool IsAvailable);
public sealed record CreateOrderResponse(long OrderNumber, OrderStatus Status, decimal Total);
public sealed record ReceiptResponse(long OrderNumber, OrderType OrderType, OrderStatus Status,
    DateTimeOffset CreatedAtUtc, string? CustomerName, string? CustomerPhone, string? DeliveryAddress,
    string? Notes, DateTimeOffset? EstimatedAtUtc, decimal Subtotal, decimal DiscountTotal, decimal DeliveryFee,
    string? CustomFeeDescription, decimal CustomFee, decimal Total,
    ReceiptPaymentResponse? Payment, List<ReceiptLineResponse> Lines);
public sealed record ReceiptPaymentResponse(PaymentMethod Method, PaymentStatus Status);
public sealed record ReceiptLineResponse(string ProductName, string? ChineseName, string VariantName, int Quantity,
    decimal UnitPrice, decimal LineTotal, string? Notes, List<ReceiptModifierResponse> Modifiers);
public sealed record ReceiptModifierResponse(string ModifierName, decimal PriceAdjustment);
public sealed record PostcodeAddressResponse(string Address, string Postcode, string? Uprn, double? DistanceMiles);
public sealed record OrderHistoryResponse(List<OrderHistoryItemResponse> Items, int Page, int PageSize, int TotalCount);
public sealed record OrderHistoryItemResponse(long OrderNumber, DateTimeOffset CreatedAtUtc, OrderType OrderType,
    OrderStatus Status, string? CustomerName, string? CustomerPhone, decimal Total, int ItemCount,
    ReceiptPaymentResponse? Payment);
public sealed record MenuImportRequest(List<MenuImportCategory> Categories);
public sealed record MenuImportCategory(string Name, List<MenuImportProduct> Products);
public sealed record MenuImportProduct(string Name, decimal Price, string? Description);

public sealed record ReorderRequest(List<Guid>? Ids);

using AnisePOS.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Data;

public sealed class AniseDbContext(DbContextOptions<AniseDbContext> options) : DbContext(options)
{
    public DbSet<Promotion> Promotions => Set<Promotion>();
    public DbSet<PromoRedemption> PromoRedemptions => Set<PromoRedemption>();
    public DbSet<DeliverySettings> DeliverySettings => Set<DeliverySettings>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<ModifierGroup> ModifierGroups => Set<ModifierGroup>();
    public DbSet<Modifier> Modifiers => Set<Modifier>();
    public DbSet<ProductModifierGroup> ProductModifierGroups => Set<ProductModifierGroup>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerAddress> CustomerAddresses => Set<CustomerAddress>();
    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<OrderLineModifier> OrderLineModifiers => Set<OrderLineModifier>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<WebsiteCheckout> WebsiteCheckouts => Set<WebsiteCheckout>();
    public DbSet<WebsiteConfirmation> WebsiteConfirmations => Set<WebsiteConfirmation>();
    public DbSet<StaffPushSubscription> StaffPushSubscriptions => Set<StaffPushSubscription>();
    public DbSet<StaffPushDelivery> StaffPushDeliveries => Set<StaffPushDelivery>();
    public DbSet<OrderStatusHistory> OrderStatusHistory => Set<OrderStatusHistory>();
    public DbSet<Refund> Refunds => Set<Refund>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        foreach (var type in builder.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
        {
            builder.Entity(type.ClrType).Property(nameof(Entity.CreatedAtUtc)).HasPrecision(0);
            builder.Entity(type.ClrType).Property(nameof(Entity.UpdatedAtUtc)).HasPrecision(0);
        }

        builder.Entity<PromoRedemption>(e => { e.HasIndex(x => new { x.Code, x.Phone }); e.Property(x => x.Code).HasMaxLength(40); e.Property(x => x.Phone).HasMaxLength(20); });
        builder.Entity<StaffPushSubscription>(e => { e.HasIndex(x => x.EndpointHash).IsUnique(); e.Property(x => x.EndpointHash).HasMaxLength(64); e.Property(x => x.StaffId).HasMaxLength(100); });
        builder.Entity<StaffPushDelivery>(e => { e.HasIndex(x => new { x.SubscriptionId, x.OrderId }).IsUnique(); e.Property(x => x.State).HasMaxLength(16); e.Property(x => x.NextAttemptAtUtc).HasPrecision(0); });
        builder.Entity<DeliverySettings>(e => { e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); e.Property(x => x.Mode).HasMaxLength(16); e.Property(x => x.RadiusMiles).HasPrecision(6, 2); e.Property(x => x.Fee).HasPrecision(10, 2); });
        builder.Entity<Category>(e => { e.HasIndex(x => x.Slug).IsUnique(); e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.Slug).HasMaxLength(120); });
        builder.Entity<ProductImage>(e => { e.HasKey(x => x.ProductId); e.Property(x => x.ContentType).HasMaxLength(20); e.HasOne<Product>().WithOne().HasForeignKey<ProductImage>(x => x.ProductId).OnDelete(DeleteBehavior.Cascade); });
        builder.Entity<Product>(e => { e.HasIndex(x => x.Slug).IsUnique(); e.Property(x => x.Name).HasMaxLength(150); e.Property(x => x.Slug).HasMaxLength(170); e.Property(x => x.Description).HasMaxLength(1000); e.Property(x => x.ChineseName).HasMaxLength(150); });
        builder.Entity<Product>().HasOne<Category>().WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<Product>().HasMany(p => p.Categories).WithMany(c => c.Products)
            .UsingEntity<Dictionary<string, object>>("ProductCategories",
                r => r.HasOne<Category>().WithMany().HasForeignKey("CategoryId").OnDelete(DeleteBehavior.Restrict),
                l => l.HasOne<Product>().WithMany().HasForeignKey("ProductId").OnDelete(DeleteBehavior.Cascade),
                j => { j.HasKey("ProductId", "CategoryId"); j.IndexerProperty<int?>("DisplayOrder"); });
        builder.Entity<ProductVariant>(e => { e.HasIndex(x => x.Sku).IsUnique(); e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.Sku).HasMaxLength(80); e.Property(x => x.Price).HasPrecision(10, 2); });
        builder.Entity<ModifierGroup>(e => e.Property(x => x.Name).HasMaxLength(100));
        builder.Entity<Modifier>(e => { e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.PriceAdjustment).HasPrecision(10, 2); });
        builder.Entity<ProductModifierGroup>(e => { e.HasKey(x => new { x.ProductId, x.ModifierGroupId }); e.HasOne(x => x.Product).WithMany(x => x.ModifierGroups).HasForeignKey(x => x.ProductId); e.HasOne(x => x.ModifierGroup).WithMany(x => x.Products).HasForeignKey(x => x.ModifierGroupId); });
        builder.Entity<Customer>(e => { e.HasIndex(x => x.Email).HasFilter("[Email] IS NOT NULL"); e.Property(x => x.FirstName).HasMaxLength(80); e.Property(x => x.LastName).HasMaxLength(80); e.Property(x => x.Email).HasMaxLength(254); e.Property(x => x.Phone).HasMaxLength(30); });
        builder.Entity<StaffUser>(e => { e.HasIndex(x => x.Email).IsUnique(); e.Property(x => x.Email).HasMaxLength(254); e.Property(x => x.DisplayName).HasMaxLength(100); e.Property(x => x.PasswordHash).HasMaxLength(500); e.Property(x => x.LastSignedInAtUtc).HasPrecision(0); });
        builder.Entity<CustomerAddress>(e => { e.Property(x => x.Label).HasMaxLength(50); e.Property(x => x.Line1).HasMaxLength(150); e.Property(x => x.Line2).HasMaxLength(150); e.Property(x => x.City).HasMaxLength(100); e.Property(x => x.Postcode).HasMaxLength(20); });
        builder.Entity<Order>(e => { e.HasIndex(x => x.OrderNumber).IsUnique(); e.HasIndex(x => new { x.Status, x.CreatedAtUtc }); e.Property(x => x.OrderNumber).ValueGeneratedNever(); e.HasOne(x => x.Customer).WithMany(x => x.Orders).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict); e.Property(x => x.CustomerName).HasMaxLength(160); e.Property(x => x.CustomerPhone).HasMaxLength(30); e.Property(x => x.Notes).HasMaxLength(1000); e.Property(x => x.DeliveryAddress).HasMaxLength(600); e.Property(x => x.EstimatedAtUtc).HasPrecision(0); e.Property(x => x.Subtotal).HasPrecision(10, 2); e.Property(x => x.DiscountTotal).HasPrecision(10, 2); e.Property(x => x.DeliveryFee).HasPrecision(10, 2); e.Property(x => x.CustomFeeDescription).HasMaxLength(160); e.Property(x => x.CustomFee).HasPrecision(10, 2); e.Property(x => x.Total).HasPrecision(10, 2); });
        builder.Entity<OrderLine>(e => { e.Ignore(x => x.IsVegan); e.Property(x => x.ProductName).HasMaxLength(150); e.Property(x => x.VariantName).HasMaxLength(100); e.Property(x => x.UnitPrice).HasPrecision(10, 2); e.Property(x => x.LineTotal).HasPrecision(10, 2); e.Property(x => x.Notes).HasMaxLength(500); });
        builder.Entity<OrderLineModifier>(e => { e.Property(x => x.ModifierName).HasMaxLength(100); e.Property(x => x.PriceAdjustment).HasPrecision(10, 2); });
        builder.Entity<Payment>(e => { e.HasIndex(x => new { x.Provider, x.ProviderReference }).HasFilter("[ProviderReference] IS NOT NULL"); e.Property(x => x.Amount).HasPrecision(10, 2); e.Property(x => x.Provider).HasMaxLength(50); e.Property(x => x.ProviderReference).HasMaxLength(150); e.Property(x => x.PaidAtUtc).HasPrecision(0); });
        builder.Entity<WebsiteCheckout>(e => { e.HasIndex(x => x.RequestId).IsUnique(); e.HasIndex(x => x.StripeSessionId).IsUnique().HasFilter("[StripeSessionId] IS NOT NULL"); e.Property(x => x.RequestHash).HasMaxLength(64); e.Property(x => x.Total).HasPrecision(10, 2); e.Property(x => x.StripeSessionId).HasMaxLength(255); e.Property(x => x.StripeSessionUrl).HasMaxLength(2048); e.Property(x => x.ReconciliationError).HasMaxLength(300); e.Property(x => x.ReconciliationCheckedAtUtc).HasPrecision(0); });
        builder.Entity<WebsiteConfirmation>(e => { e.HasIndex(x => new { x.OrderId, x.Kind }).IsUnique(); e.Property(x => x.Kind).HasMaxLength(20); e.Property(x => x.State).HasMaxLength(20); e.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict); });
        builder.Entity<OrderStatusHistory>(e => { e.HasIndex(x => new { x.OrderId, x.CreatedAtUtc }); e.Property(x => x.Note).HasMaxLength(500); e.Property(x => x.ChangedBy).HasMaxLength(100); });
        builder.Entity<Refund>(e => { e.HasIndex(x => new { x.OrderId, x.CreatedAtUtc }); e.Property(x => x.Amount).HasPrecision(10, 2); e.Property(x => x.Reason).HasMaxLength(200); e.Property(x => x.ProviderReference).HasMaxLength(150); e.Property(x => x.Note).HasMaxLength(500); e.Property(x => x.CompletedAtUtc).HasPrecision(0); e.HasOne(x => x.Order).WithMany(x => x.Refunds).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict); });
    }
}

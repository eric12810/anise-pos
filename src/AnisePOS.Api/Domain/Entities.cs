namespace AnisePOS.Api.Domain;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}

public sealed class Category : Entity
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public bool IsAvailable { get; set; } = true;
    public ICollection<Product> Products { get; set; } = new List<Product>();
}

public sealed class Product : Entity
{
    public Guid CategoryId { get; set; }
    public ICollection<Category> Categories { get; set; } = new List<Category>();
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? ChineseName { get; set; }
    public string? Description { get; set; }
    public bool IsVegan { get; set; }
    public string? VeganSection { get; set; }
    public bool IsAvailable { get; set; } = true;
    public int DisplayOrder { get; set; }
    public ICollection<ProductVariant> Variants { get; set; } = new List<ProductVariant>();
    public ICollection<ProductModifierGroup> ModifierGroups { get; set; } = new List<ProductModifierGroup>();
}

public sealed class ProductVariant : Entity
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public bool IsAvailable { get; set; } = true;
    public int DisplayOrder { get; set; }
}

public sealed class ModifierGroup : Entity
{
    public string Name { get; set; } = string.Empty;
    public int MinimumSelections { get; set; }
    public int MaximumSelections { get; set; } = 1;
    public ICollection<Modifier> Modifiers { get; set; } = new List<Modifier>();
    public ICollection<ProductModifierGroup> Products { get; set; } = new List<ProductModifierGroup>();
}

public sealed class Modifier : Entity
{
    public Guid ModifierGroupId { get; set; }
    public ModifierGroup ModifierGroup { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public decimal PriceAdjustment { get; set; }
    public bool IsAvailable { get; set; } = true;
    public int DisplayOrder { get; set; }
}

public sealed class ProductModifierGroup
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid ModifierGroupId { get; set; }
    public ModifierGroup ModifierGroup { get; set; } = null!;
    public int DisplayOrder { get; set; }
}

public sealed class Customer : Entity
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public ICollection<CustomerAddress> Addresses { get; set; } = new List<CustomerAddress>();
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}

public sealed class CustomerAddress : Entity
{
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string Label { get; set; } = "Address";
    public string Line1 { get; set; } = string.Empty;
    public string? Line2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string? Postcode { get; set; }
    public bool IsDefault { get; set; }
}

public enum OrderType { Collection, Delivery }
public enum OrderStatus { Draft, Placed, Accepted, Preparing, Ready, Completed, Cancelled, Refunded }
public enum PaymentMethod { Cash, Card, Online, GiftCard, Other }
public enum PaymentStatus { Pending, Authorised, Paid, Failed, Voided, Refunded, PartiallyRefunded, RefundPending }
public enum StaffRole { Owner, Manager, Cashier }

public sealed class StaffUser : Entity
{
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public StaffRole Role { get; set; } = StaffRole.Cashier;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastSignedInAtUtc { get; set; }
}

public sealed class Order : Entity
{
    public long OrderNumber { get; set; }
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public OrderType OrderType { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Draft;
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? Notes { get; set; }
    public string? DeliveryAddress { get; set; }
    public DateTimeOffset? EstimatedAtUtc { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal DeliveryFee { get; set; }
    public string? CustomFeeDescription { get; set; }
    public decimal CustomFee { get; set; }
    public decimal Total { get; set; }
    public ICollection<OrderLine> Lines { get; set; } = new List<OrderLine>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public ICollection<OrderStatusHistory> StatusHistory { get; set; } = new List<OrderStatusHistory>();
}

public sealed class WebsiteConfirmation : Entity
{
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public string ProtectedPayload { get; set; } = string.Empty;
    public string Kind { get; set; } = "Received";
    public string State { get; set; } = "Pending";
    public DateTimeOffset? SentAtUtc { get; set; }
}

public sealed class StaffPushSubscription : Entity
{
    public string EndpointHash { get; set; } = "";
    public string ProtectedSubscription { get; set; } = "";
    public string StaffId { get; set; } = "";
}

public sealed class StaffPushDelivery : Entity
{
    public Guid SubscriptionId { get; set; }
    public Guid OrderId { get; set; }
    public string State { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
}

public sealed class OrderLine : Entity
{
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public Guid? ProductId { get; set; }
    public Guid? ProductVariantId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string VariantName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal LineTotal { get; set; }
    public string? Notes { get; set; }
    public ICollection<OrderLineModifier> Modifiers { get; set; } = new List<OrderLineModifier>();
}

public sealed class OrderLineModifier : Entity
{
    public Guid OrderLineId { get; set; }
    public OrderLine OrderLine { get; set; } = null!;
    public Guid? ModifierId { get; set; }
    public string ModifierName { get; set; } = string.Empty;
    public decimal PriceAdjustment { get; set; }
    public int Quantity { get; set; } = 1;
}

public sealed class Payment : Entity
{
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public PaymentMethod Method { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public decimal Amount { get; set; }
    public string? Provider { get; set; }
    public string? ProviderReference { get; set; }
    public DateTimeOffset? PaidAtUtc { get; set; }
}

public sealed class OrderStatusHistory : Entity
{
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public OrderStatus Status { get; set; }
    public string? Note { get; set; }
    public string? ChangedBy { get; set; }
}

public sealed class ProductImage
{
    public Guid ProductId { get; set; }
    public byte[] Data { get; set; } = [];
    public string ContentType { get; set; } = string.Empty;
}

// Website card orders stay outside the kitchen queue until payment is verified.
public sealed class WebsiteCheckout : Entity
{
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public string? StripeSessionId { get; set; }
    public string? StripeSessionUrl { get; set; }
    public Guid? CompletedOrderId { get; set; }
}

public sealed class DeliverySettings
{
    public int Id { get; set; }
    public string Mode { get; set; } = "radius";
    public string SectorsJson { get; set; } = "[]";
    public string RadiusBandsJson { get; set; } = "[]";
    public string? OrderingAvailabilityJson { get; set; }
    public string? PromoCodesJson { get; set; }
    public string? PrinterIpAddress { get; set; }
    public string? OrderSound { get; set; }
    public decimal RadiusMiles { get; set; }
    public decimal Fee { get; set; }
}

public sealed class PromoRedemption : Entity
{
    public string Code { get; set; } = "";
    public string Phone { get; set; } = "";
    public Guid OrderId { get; set; }
}

public sealed class Promotion : Entity
{
    public string SettingsJson { get; set; } = "{}";
    public byte[]? ImageData { get; set; }
    public string? ImageContentType { get; set; }
}

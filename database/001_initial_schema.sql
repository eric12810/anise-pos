/* Anise POS initial schema — SQL Server 2019 / compatibility level 150. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

CREATE TABLE dbo.Categories (
    Id uniqueidentifier NOT NULL PRIMARY KEY, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL,
    Name nvarchar(100) NOT NULL, Slug nvarchar(120) NOT NULL, DisplayOrder int NOT NULL DEFAULT 0, IsAvailable bit NOT NULL DEFAULT 1,
    CONSTRAINT UQ_Categories_Slug UNIQUE (Slug)
);
CREATE TABLE dbo.Products (
    Id uniqueidentifier NOT NULL PRIMARY KEY, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL,
    CategoryId uniqueidentifier NOT NULL, Name nvarchar(150) NOT NULL, Slug nvarchar(170) NOT NULL, Description nvarchar(1000) NULL,
    IsAvailable bit NOT NULL DEFAULT 1, DisplayOrder int NOT NULL DEFAULT 0,
    CONSTRAINT FK_Products_Categories FOREIGN KEY (CategoryId) REFERENCES dbo.Categories(Id),
    CONSTRAINT UQ_Products_Slug UNIQUE (Slug)
);
CREATE TABLE dbo.ProductVariants (
    Id uniqueidentifier NOT NULL PRIMARY KEY, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL,
    ProductId uniqueidentifier NOT NULL, Name nvarchar(100) NOT NULL, Sku nvarchar(80) NOT NULL, Price decimal(10,2) NOT NULL,
    IsAvailable bit NOT NULL DEFAULT 1, DisplayOrder int NOT NULL DEFAULT 0,
    CONSTRAINT FK_ProductVariants_Products FOREIGN KEY (ProductId) REFERENCES dbo.Products(Id),
    CONSTRAINT UQ_ProductVariants_Sku UNIQUE (Sku)
);
CREATE TABLE dbo.ModifierGroups (
    Id uniqueidentifier NOT NULL PRIMARY KEY, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL,
    Name nvarchar(100) NOT NULL, MinimumSelections int NOT NULL DEFAULT 0, MaximumSelections int NOT NULL DEFAULT 1,
    CONSTRAINT CK_ModifierGroups_Selections CHECK (MinimumSelections >= 0 AND MaximumSelections >= MinimumSelections)
);
CREATE TABLE dbo.Modifiers (
    Id uniqueidentifier NOT NULL PRIMARY KEY, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL,
    ModifierGroupId uniqueidentifier NOT NULL, Name nvarchar(100) NOT NULL, PriceAdjustment decimal(10,2) NOT NULL DEFAULT 0,
    IsAvailable bit NOT NULL DEFAULT 1, DisplayOrder int NOT NULL DEFAULT 0,
    CONSTRAINT FK_Modifiers_ModifierGroups FOREIGN KEY (ModifierGroupId) REFERENCES dbo.ModifierGroups(Id)
);
CREATE TABLE dbo.ProductModifierGroups (
    ProductId uniqueidentifier NOT NULL, ModifierGroupId uniqueidentifier NOT NULL, DisplayOrder int NOT NULL DEFAULT 0,
    CONSTRAINT PK_ProductModifierGroups PRIMARY KEY (ProductId, ModifierGroupId),
    CONSTRAINT FK_ProductModifierGroups_Products FOREIGN KEY (ProductId) REFERENCES dbo.Products(Id),
    CONSTRAINT FK_ProductModifierGroups_ModifierGroups FOREIGN KEY (ModifierGroupId) REFERENCES dbo.ModifierGroups(Id)
);
CREATE TABLE dbo.Customers (
    Id uniqueidentifier NOT NULL PRIMARY KEY, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL,
    FirstName nvarchar(80) NULL, LastName nvarchar(80) NULL, Email nvarchar(254) NULL, Phone nvarchar(30) NULL
);
CREATE UNIQUE INDEX UX_Customers_Email ON dbo.Customers(Email) WHERE Email IS NOT NULL;
CREATE TABLE dbo.CustomerAddresses (
    Id uniqueidentifier NOT NULL PRIMARY KEY, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL,
    CustomerId uniqueidentifier NOT NULL, Label nvarchar(50) NOT NULL, Line1 nvarchar(150) NOT NULL, Line2 nvarchar(150) NULL,
    City nvarchar(100) NOT NULL, Postcode nvarchar(20) NULL, IsDefault bit NOT NULL DEFAULT 0,
    CONSTRAINT FK_CustomerAddresses_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers(Id)
);
CREATE TABLE dbo.Orders (
    Id uniqueidentifier NOT NULL PRIMARY KEY, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL,
    OrderNumber bigint IDENTITY(1000,1) NOT NULL, CustomerId uniqueidentifier NULL, OrderType int NOT NULL, Status int NOT NULL,
    CustomerName nvarchar(160) NULL, CustomerPhone nvarchar(30) NULL, Notes nvarchar(1000) NULL, DeliveryAddress nvarchar(600) NULL,
    Subtotal decimal(10,2) NOT NULL DEFAULT 0, DiscountTotal decimal(10,2) NOT NULL DEFAULT 0, DeliveryFee decimal(10,2) NOT NULL DEFAULT 0, Total decimal(10,2) NOT NULL DEFAULT 0,
    CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers(Id),
    CONSTRAINT UQ_Orders_OrderNumber UNIQUE (OrderNumber)
);
CREATE INDEX IX_Orders_Status_CreatedAtUtc ON dbo.Orders(Status, CreatedAtUtc);
CREATE TABLE dbo.OrderLines (
    Id uniqueidentifier NOT NULL PRIMARY KEY, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL,
    OrderId uniqueidentifier NOT NULL, ProductId uniqueidentifier NULL, ProductVariantId uniqueidentifier NULL,
    ProductName nvarchar(150) NOT NULL, VariantName nvarchar(100) NOT NULL, UnitPrice decimal(10,2) NOT NULL, Quantity int NOT NULL, LineTotal decimal(10,2) NOT NULL,
    CONSTRAINT FK_OrderLines_Orders FOREIGN KEY (OrderId) REFERENCES dbo.Orders(Id),
    CONSTRAINT CK_OrderLines_Quantity CHECK (Quantity > 0)
);
CREATE TABLE dbo.OrderLineModifiers (
    Id uniqueidentifier NOT NULL PRIMARY KEY, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL,
    OrderLineId uniqueidentifier NOT NULL, ModifierId uniqueidentifier NULL, ModifierName nvarchar(100) NOT NULL,
    PriceAdjustment decimal(10,2) NOT NULL, Quantity int NOT NULL DEFAULT 1,
    CONSTRAINT FK_OrderLineModifiers_OrderLines FOREIGN KEY (OrderLineId) REFERENCES dbo.OrderLines(Id),
    CONSTRAINT CK_OrderLineModifiers_Quantity CHECK (Quantity > 0)
);
CREATE TABLE dbo.Payments (
    Id uniqueidentifier NOT NULL PRIMARY KEY, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL,
    OrderId uniqueidentifier NOT NULL, Method int NOT NULL, Status int NOT NULL, Amount decimal(10,2) NOT NULL,
    Provider nvarchar(50) NULL, ProviderReference nvarchar(150) NULL, PaidAtUtc datetimeoffset(0) NULL,
    CONSTRAINT FK_Payments_Orders FOREIGN KEY (OrderId) REFERENCES dbo.Orders(Id)
);
CREATE INDEX IX_Payments_Provider_Reference ON dbo.Payments(Provider, ProviderReference) WHERE ProviderReference IS NOT NULL;
CREATE TABLE dbo.OrderStatusHistory (
    Id uniqueidentifier NOT NULL PRIMARY KEY, CreatedAtUtc datetimeoffset(0) NOT NULL, UpdatedAtUtc datetimeoffset(0) NULL,
    OrderId uniqueidentifier NOT NULL, Status int NOT NULL, Note nvarchar(500) NULL, ChangedBy nvarchar(100) NULL,
    CONSTRAINT FK_OrderStatusHistory_Orders FOREIGN KEY (OrderId) REFERENCES dbo.Orders(Id)
);
CREATE INDEX IX_OrderStatusHistory_Order_CreatedAtUtc ON dbo.OrderStatusHistory(OrderId, CreatedAtUtc);

COMMIT TRANSACTION;

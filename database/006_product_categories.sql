-- Run once before deploying the multiple-category menu update.
-- Preserve all existing product assignments in the new membership table.
IF OBJECT_ID('dbo.ProductCategories', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductCategories (
        ProductId uniqueidentifier NOT NULL,
        CategoryId uniqueidentifier NOT NULL,
        CONSTRAINT PK_ProductCategories PRIMARY KEY (ProductId, CategoryId),
        CONSTRAINT FK_ProductCategories_Products FOREIGN KEY (ProductId) REFERENCES dbo.Products(Id) ON DELETE CASCADE,
        CONSTRAINT FK_ProductCategories_Categories FOREIGN KEY (CategoryId) REFERENCES dbo.Categories(Id)
    );
    INSERT INTO dbo.ProductCategories (ProductId, CategoryId)
        SELECT Id, CategoryId FROM dbo.Products;
    CREATE INDEX IX_ProductCategories_CategoryId ON dbo.ProductCategories (CategoryId);
END;

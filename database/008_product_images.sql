IF OBJECT_ID('dbo.ProductImages', 'U') IS NULL
    CREATE TABLE dbo.ProductImages (
        ProductId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES dbo.Products(Id) ON DELETE CASCADE,
        Data varbinary(max) NOT NULL,
        ContentType nvarchar(20) NOT NULL
    );

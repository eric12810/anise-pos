IF COL_LENGTH('dbo.Products', 'IsVegan') IS NULL
    ALTER TABLE dbo.Products ADD IsVegan bit NOT NULL CONSTRAINT DF_Products_IsVegan DEFAULT 0;

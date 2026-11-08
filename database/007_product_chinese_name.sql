-- Add an optional Chinese name without changing existing dish names.
IF COL_LENGTH('dbo.Products', 'ChineseName') IS NULL
    ALTER TABLE dbo.Products ADD ChineseName nvarchar(150) NULL;

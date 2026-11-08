-- Existing entries retain their previous product order until reordered.
IF COL_LENGTH('dbo.ProductCategories', 'DisplayOrder') IS NULL
    ALTER TABLE dbo.ProductCategories ADD DisplayOrder int NULL;

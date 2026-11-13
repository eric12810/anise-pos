-- Review and apply to staging only after backup and schema comparison.
IF COL_LENGTH('dbo.WebsiteCheckouts', 'ReconciliationError') IS NULL
    ALTER TABLE dbo.WebsiteCheckouts ADD ReconciliationError nvarchar(300) NULL;
IF COL_LENGTH('dbo.WebsiteCheckouts', 'ReconciliationCheckedAtUtc') IS NULL
    ALTER TABLE dbo.WebsiteCheckouts ADD ReconciliationCheckedAtUtc datetimeoffset(0) NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Payments_StripeCheckout_Reference' AND object_id = OBJECT_ID('dbo.Payments'))
    CREATE UNIQUE INDEX UX_Payments_StripeCheckout_Reference ON dbo.Payments(Provider, ProviderReference)
    WHERE Provider = 'StripeCheckout' AND ProviderReference IS NOT NULL;

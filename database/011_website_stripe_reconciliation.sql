-- Apply to staging only after confirming DB_NAME() = 'anisedb'.
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS (
        SELECT 1
        FROM dbo.Payments
        WHERE Provider = 'StripeCheckout' AND ProviderReference IS NOT NULL
        GROUP BY ProviderReference
        HAVING COUNT_BIG(*) > 1
    )
        THROW 51011, 'Duplicate Stripe Checkout payment references exist.', 1;

    IF COL_LENGTH('dbo.WebsiteCheckouts', 'ReconciliationError') IS NULL
        ALTER TABLE dbo.WebsiteCheckouts ADD ReconciliationError nvarchar(300) NULL;
    IF COL_LENGTH('dbo.WebsiteCheckouts', 'ReconciliationCheckedAtUtc') IS NULL
        ALTER TABLE dbo.WebsiteCheckouts ADD ReconciliationCheckedAtUtc datetimeoffset(0) NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Payments_StripeCheckout_Reference' AND object_id = OBJECT_ID('dbo.Payments'))
        CREATE UNIQUE INDEX UX_Payments_StripeCheckout_Reference ON dbo.Payments(Provider, ProviderReference)
        WHERE Provider = 'StripeCheckout' AND ProviderReference IS NOT NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.Orders', 'CustomFeeDescription') IS NULL
    ALTER TABLE dbo.Orders ADD CustomFeeDescription nvarchar(160) NULL;

IF COL_LENGTH('dbo.Orders', 'CustomFee') IS NULL
    ALTER TABLE dbo.Orders ADD CustomFee decimal(10,2) NOT NULL CONSTRAINT DF_Orders_CustomFee DEFAULT 0;

COMMIT TRANSACTION;

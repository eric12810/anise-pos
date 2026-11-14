-- Staging migration for cancellation, reopening and refund records.
-- Review against the live staging schema before applying; do not run on production.
IF COL_LENGTH('dbo.Orders', 'CancelledAtUtc') IS NULL ALTER TABLE dbo.Orders ADD CancelledAtUtc datetimeoffset(0) NULL;
IF COL_LENGTH('dbo.Orders', 'CancellationReason') IS NULL ALTER TABLE dbo.Orders ADD CancellationReason nvarchar(200) NULL;
IF COL_LENGTH('dbo.Orders', 'ReopenedAtUtc') IS NULL ALTER TABLE dbo.Orders ADD ReopenedAtUtc datetimeoffset(0) NULL;
IF OBJECT_ID('dbo.Refunds', 'U') IS NULL
BEGIN
 CREATE TABLE dbo.Refunds (
   Id uniqueidentifier NOT NULL CONSTRAINT PK_Refunds PRIMARY KEY,
   CreatedAtUtc datetimeoffset(0) NOT NULL,
   UpdatedAtUtc datetimeoffset(0) NULL,
   OrderId uniqueidentifier NOT NULL,
   Amount decimal(10,2) NOT NULL,
   Method int NOT NULL,
   Status int NOT NULL,
   Reason nvarchar(200) NOT NULL,
   ProviderReference nvarchar(150) NULL,
   CompletedAtUtc datetimeoffset(0) NULL,
   Note nvarchar(500) NULL,
   CONSTRAINT FK_Refunds_Orders_OrderId FOREIGN KEY (OrderId) REFERENCES dbo.Orders(Id)
 );
 CREATE INDEX IX_Refunds_OrderId_CreatedAtUtc ON dbo.Refunds(OrderId, CreatedAtUtc);
END

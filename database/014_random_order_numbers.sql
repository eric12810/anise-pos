-- Run only after a verified staging backup, schema preflight and stopped order writes.
-- Existing numbers and GUID primary keys remain unchanged. SQL Server cannot ALTER away IDENTITY.
SET XACT_ABORT ON;
IF DB_NAME() <> N'anisedb' THROW 51000, 'This migration is restricted to verified staging.', 1;
IF OBJECT_ID(N'dbo.Orders', N'U') IS NULL THROW 51001, 'Orders table missing.', 1;
IF COLUMNPROPERTY(OBJECT_ID(N'dbo.Orders'), N'OrderNumber', 'IsIdentity') <> 1
    THROW 51002, 'OrderNumber is not an identity column.', 1;
IF COL_LENGTH(N'dbo.Orders', N'OrderNumberNew') IS NOT NULL
    THROW 51003, 'Temporary column already exists.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'dbo.Orders') AND name = N'UQ_Orders_OrderNumber')
    THROW 51004, 'Expected unique constraint missing.', 1;
IF EXISTS (SELECT 1 FROM sys.foreign_key_columns WHERE referenced_object_id = OBJECT_ID(N'dbo.Orders') AND referenced_column_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.Orders'), N'OrderNumber', 'ColumnId'))
    THROW 51005, 'OrderNumber has an unexpected foreign key dependency.', 1;
IF EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id=ic.object_id AND i.index_id=ic.index_id
           WHERE ic.object_id=OBJECT_ID(N'dbo.Orders') AND ic.column_id=COLUMNPROPERTY(OBJECT_ID(N'dbo.Orders'), N'OrderNumber', 'ColumnId')
             AND i.name NOT IN (N'UQ_Orders_OrderNumber', N'IX_Orders_OrderNumber'))
    THROW 51006, 'OrderNumber has an unexpected index dependency.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @before bigint = (SELECT COUNT_BIG(*) FROM dbo.Orders);
    ALTER TABLE dbo.Orders ADD OrderNumberNew bigint NULL;
    -- Compile statements using the new column only after ALTER TABLE has run.
    EXEC sys.sp_executesql N'UPDATE dbo.Orders SET OrderNumberNew = OrderNumber';
    EXEC sys.sp_executesql N'IF EXISTS (SELECT 1 FROM dbo.Orders WHERE OrderNumberNew IS NULL OR OrderNumberNew <> OrderNumber)
        THROW 51007, ''Order number copy failed.'', 1;';
    ALTER TABLE dbo.Orders ALTER COLUMN OrderNumberNew bigint NOT NULL;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Orders') AND name=N'IX_Orders_OrderNumber')
        DROP INDEX IX_Orders_OrderNumber ON dbo.Orders;
    ALTER TABLE dbo.Orders DROP CONSTRAINT UQ_Orders_OrderNumber;
    ALTER TABLE dbo.Orders DROP COLUMN OrderNumber;
    EXEC sys.sp_rename N'dbo.Orders.OrderNumberNew', N'OrderNumber', N'COLUMN';
    ALTER TABLE dbo.Orders ADD CONSTRAINT UQ_Orders_OrderNumber UNIQUE (OrderNumber);
    IF (SELECT COUNT_BIG(*) FROM dbo.Orders) <> @before OR
       EXISTS (SELECT 1 FROM dbo.Orders WHERE OrderNumber IS NULL) OR
       COLUMNPROPERTY(OBJECT_ID(N'dbo.Orders'), N'OrderNumber', 'IsIdentity') <> 0
        THROW 51008, 'Post-migration integrity check failed.', 1;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

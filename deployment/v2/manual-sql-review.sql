-- Run in the existing VS Code connection selected for production.
-- Read-only metadata only. Do not run the POS API to discover its connection.
-- Record only non-secret identifiers and the review reference in protected evidence.

SELECT DB_NAME() AS DatabaseName, @@SERVERNAME AS ServerName;

SELECT s.name AS SchemaName, t.name AS TableName, c.name AS ColumnName,
       ty.name AS DataType, c.max_length AS MaxLengthBytes,
       c.precision AS NumericPrecision, c.scale AS NumericScale,
       c.is_nullable AS IsNullable, dc.definition AS DefaultDefinition
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
JOIN sys.columns AS c ON c.object_id = t.object_id
JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
WHERE s.name = N'dbo'
ORDER BY t.name, c.column_id;

SELECT s.name AS SchemaName, t.name AS TableName, i.name AS IndexName,
       i.is_unique AS IsUnique, i.is_primary_key AS IsPrimaryKey,
       i.has_filter AS HasFilter, i.filter_definition AS FilterDefinition,
       ic.key_ordinal AS KeyOrdinal, c.name AS ColumnName
FROM sys.indexes AS i
JOIN sys.tables AS t ON t.object_id = i.object_id
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE s.name = N'dbo' AND i.name IS NOT NULL
ORDER BY t.name, i.name, ic.key_ordinal, ic.index_column_id;

SELECT fk.name AS ForeignKeyName, parent.name AS ParentTable,
       parent_column.name AS ParentColumn, referenced.name AS ReferencedTable,
       referenced_column.name AS ReferencedColumn
FROM sys.foreign_key_columns AS fkc
JOIN sys.foreign_keys AS fk ON fk.object_id = fkc.constraint_object_id
JOIN sys.tables AS parent ON parent.object_id = fkc.parent_object_id
JOIN sys.columns AS parent_column ON parent_column.object_id = fkc.parent_object_id
    AND parent_column.column_id = fkc.parent_column_id
JOIN sys.tables AS referenced ON referenced.object_id = fkc.referenced_object_id
JOIN sys.columns AS referenced_column ON referenced_column.object_id = fkc.referenced_object_id
    AND referenced_column.column_id = fkc.referenced_column_id
ORDER BY fk.name, fkc.constraint_column_id;

SELECT CASE WHEN OBJECT_ID(N'dbo.AniseSchemaMigrations', N'U') IS NULL
            THEN 0 ELSE 1 END AS MigrationHistoryTablePresent,
       CASE WHEN EXISTS (
           SELECT 1 FROM sys.indexes
           WHERE object_id = OBJECT_ID(N'dbo.Payments', N'U')
             AND name = N'UX_Payments_StripeCheckout_Reference'
       ) THEN 1 ELSE 0 END AS RequiredPaymentIndexPresent;

-- If MigrationHistoryTablePresent is 1, inspect its recorded versions and
-- checksums with a separate SELECT, then compare them to
-- database/migration-checksums.json. Do not execute migration scripts here.

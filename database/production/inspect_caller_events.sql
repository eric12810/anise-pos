-- Read-only production preflight. Run only these metadata queries through
-- an authorised production SQL connection; no dedicated read-only login is required.
-- Record only identifiers and schema metadata; do not export customer or order rows.
IF DB_NAME() <> N'anisedatabase'
    THROW 51000, 'Expected production database anisedatabase.', 1;

SELECT DB_NAME() AS DatabaseName, @@SERVERNAME AS ServerName;

SELECT t.name AS TableName, c.name AS ColumnName, ty.name AS DataType,
       c.max_length AS MaxLength, c.is_nullable AS IsNullable, c.is_identity AS IsIdentity
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
JOIN sys.columns AS c ON c.object_id = t.object_id
JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
WHERE s.name = N'dbo' AND t.name = N'CallerEvents'
ORDER BY c.column_id;

SELECT i.name AS IndexName, i.is_unique AS IsUnique, i.is_primary_key AS IsPrimaryKey,
       ic.key_ordinal AS KeyOrdinal, c.name AS ColumnName, ic.is_descending_key AS IsDescending
FROM sys.indexes AS i
JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(N'dbo.CallerEvents', N'U') AND ic.key_ordinal > 0
ORDER BY i.name, ic.key_ordinal;

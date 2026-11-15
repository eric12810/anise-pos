-- Run only in the selected production VS Code MSSQL connection.
-- One metadata-only JSON value. Save the complete cell as private UTF-8 JSON.
SELECT CONVERT(varchar(27), CAST(SYSUTCDATETIME() AS datetime2(6)), 126) + 'Z' AS checked_at,
       DB_NAME() AS [database], @@SERVERNAME AS [server],
       JSON_QUERY((SELECT s.name AS [schema],t.name AS [table],c.name AS [column],
                          ty.name AS [type],CONVERT(bit,c.is_nullable) AS nullable,
                          c.max_length AS max_length_bytes,c.precision AS numeric_precision,
                          c.scale AS numeric_scale,dc.definition AS default_definition
                   FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
                   JOIN sys.columns c ON c.object_id=t.object_id
                   JOIN sys.types ty ON ty.user_type_id=c.user_type_id
                   LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
                   WHERE s.name=N'dbo' ORDER BY t.name,c.column_id FOR JSON PATH)) AS columns,
       JSON_QUERY((SELECT s.name AS [schema],t.name AS [table],i.name AS name,
                          CONVERT(bit,i.is_unique) AS [unique],CONVERT(bit,i.is_primary_key) AS primary_key,
                          i.filter_definition AS [filter],
                          JSON_QUERY((SELECT c.name FROM sys.index_columns ic
                                      JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                                      WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0
                                      ORDER BY ic.key_ordinal FOR JSON PATH)) AS key_rows
                   FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id
                   JOIN sys.schemas s ON s.schema_id=t.schema_id
                   WHERE s.name=N'dbo' AND i.name IS NOT NULL ORDER BY t.name,i.name FOR JSON PATH)) AS indexes,
       JSON_QUERY((SELECT fk.name AS name,pt.name AS parent_table,pc.name AS parent_column,
                          rt.name AS referenced_table,rc.name AS referenced_column
                   FROM sys.foreign_key_columns fkc
                   JOIN sys.foreign_keys fk ON fk.object_id=fkc.constraint_object_id
                   JOIN sys.tables pt ON pt.object_id=fkc.parent_object_id
                   JOIN sys.columns pc ON pc.object_id=fkc.parent_object_id AND pc.column_id=fkc.parent_column_id
                   JOIN sys.tables rt ON rt.object_id=fkc.referenced_object_id
                   JOIN sys.columns rc ON rc.object_id=fkc.referenced_object_id AND rc.column_id=fkc.referenced_column_id
                   ORDER BY fk.name,fkc.constraint_column_id FOR JSON PATH)) AS foreign_keys,
       JSON_QUERY((SELECT t.name AS [table],cc.name AS name,cc.definition AS definition
                   FROM sys.check_constraints cc JOIN sys.tables t ON t.object_id=cc.parent_object_id
                   ORDER BY t.name,cc.name FOR JSON PATH)) AS check_constraints,
       JSON_QUERY((SELECT CASE WHEN OBJECT_ID(N'dbo.AniseSchemaMigrations',N'U') IS NULL
                           THEN CONVERT(bit,0) ELSE CONVERT(bit,1) END AS available,
                          JSON_QUERY('[]') AS records FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) AS migration_history
FOR JSON PATH,WITHOUT_ARRAY_WRAPPER;

-- If dbo.AniseSchemaMigrations exists, inspect its metadata and separately
-- SELECT only version and checksum for protected review.
-- Optional after confirming columns: SELECT MigrationId AS version, Checksum AS checksum
-- FROM dbo.AniseSchemaMigrations ORDER BY MigrationId;

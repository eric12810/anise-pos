using System.Text.Json;
using AnisePOS.Api.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

// This console process never starts the API or invokes its write-capable startup path.
// The credential must belong to a separately verified read-only SQL login.
if (args.Length == 1 && args[0] == "--check-model")
{
    var offlineOptions = new DbContextOptionsBuilder<AniseDbContext>().UseSqlServer("Server=localhost;Database=offline-model-check").Options;
    await using var offlineContext = new AniseDbContext(offlineOptions);
    Console.WriteLine("Mapped SQL columns: " + ProductionSchemaRequirements.MappedColumns(offlineContext.Model).Length);
    return;
}
if (args.Length == 2 && args[0] == "--schema-json")
{
    var offlineOptions = new DbContextOptionsBuilder<AniseDbContext>().UseSqlServer("Server=localhost;Database=offline-model-check").Options;
    await using var offlineContext = new AniseDbContext(offlineOptions);
    var tables = offlineContext.Model.GetEntityTypes().Where(e => e.GetTableName() is not null).ToArray();
    var columns = tables.SelectMany(e => {
        var store = StoreObjectIdentifier.Table(e.GetTableName()!, e.GetSchema());
        return e.GetProperties().Select(p => $"{e.GetSchema() ?? "dbo"}.{e.GetTableName()}.{p.GetColumnName(store)}");
    }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
    if (columns.Length == 0 || columns.Any(x => x.EndsWith('.')))
        throw new InvalidOperationException("EF model columns unavailable");
    var indexes = tables.SelectMany(e => e.GetIndexes().Select(i => $"{e.GetSchema() ?? "dbo"}.{e.GetTableName()}.{i.GetDatabaseName()}"))
        .Where(x => !x.EndsWith('.')).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
    var mappedTypes = tables.SelectMany(e => {
        var store = StoreObjectIdentifier.Table(e.GetTableName()!, e.GetSchema());
        return e.GetProperties().Select(p => new {
            name = $"{e.GetSchema() ?? "dbo"}.{e.GetTableName()}.{p.GetColumnName(store)}",
            type = p.GetColumnType()?.ToLowerInvariant(),
            nullable = p.IsNullable
        });
    }).ToArray();
    var mappedIndexes = tables.SelectMany(e => e.GetIndexes().Select(i => new {
        name = $"{e.GetSchema() ?? "dbo"}.{e.GetTableName()}.{i.GetDatabaseName()}",
        unique = i.IsUnique,
        keys = i.Properties.Select(p => p.GetColumnName(StoreObjectIdentifier.Table(e.GetTableName()!, e.GetSchema()))).ToArray()
    })).ToArray();
    var mappedForeignKeys = tables.SelectMany(e => e.GetForeignKeys().Where(f => f.PrincipalEntityType.GetTableName() is not null)
        .SelectMany(f => f.Properties.Zip(f.PrincipalKey.Properties, (p, pp) => new {
            parent_table = e.GetTableName(),
            parent_column = p.GetColumnName(StoreObjectIdentifier.Table(e.GetTableName()!, e.GetSchema())),
            referenced_table = f.PrincipalEntityType.GetTableName(),
            referenced_column = pp.GetColumnName(StoreObjectIdentifier.Table(f.PrincipalEntityType.GetTableName()!, f.PrincipalEntityType.GetSchema()))
        }))).ToArray();
    var mappedPrimaryKeys = tables.Select(e => new {
        table = e.GetTableName(),
        keys = e.FindPrimaryKey()?.Properties.Select(p => p.GetColumnName(StoreObjectIdentifier.Table(e.GetTableName()!, e.GetSchema()))).ToArray()
    }).ToArray();
    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { columns, indexes, mappedTypes, mappedIndexes, mappedForeignKeys, mappedPrimaryKeys }));
    Console.WriteLine("Offline EF schema requirements written");
    return;
}
if (args.Length != 2 || args[0] != "--output")
    throw new InvalidOperationException("Usage: DbProbe --output result.json | --check-model | --schema-json expected.json");

try
{
    var raw = Environment.GetEnvironmentVariable("V2_SQL_CONNECTION")
        ?? throw new InvalidOperationException("Read-only SQL connection secret missing");
    var builder = new SqlConnectionStringBuilder(raw)
    {
        ApplicationIntent = ApplicationIntent.ReadOnly,
        Encrypt = true,
        ConnectTimeout = 15
    };
    await using var connection = new SqlConnection(builder.ConnectionString);
    await connection.OpenAsync();

    string database, server;
    await using (var command = new SqlCommand("SELECT DB_NAME(), @@SERVERNAME", connection))
    await using (var reader = await command.ExecuteReaderAsync())
    {
        if (!await reader.ReadAsync()) throw new InvalidOperationException("SQL identity unavailable");
        database = reader.GetString(0);
        server = reader.GetString(1);
    }

    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using (var command = new SqlCommand("""
        SELECT CONCAT(s.name, '.', t.name, '.', c.name)
        FROM sys.schemas s JOIN sys.tables t ON t.schema_id = s.schema_id
        JOIN sys.columns c ON c.object_id = t.object_id
        """, connection))
    await using (var reader = await command.ExecuteReaderAsync())
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));

    var options = new DbContextOptionsBuilder<AniseDbContext>().UseSqlServer(builder.ConnectionString).Options;
    await using var context = new AniseDbContext(options);
    var required = ProductionSchemaRequirements.MappedColumns(context.Model);
    var missing = required.Where(column => !columns.Contains(column)).OrderBy(x => x).ToArray();
    var migrationColumns = new[] {
        "dbo.Orders.CancelledAtUtc", "dbo.Orders.CancellationReason", "dbo.Orders.ReopenedAtUtc",
        "dbo.Refunds.Id", "dbo.Refunds.OrderId", "dbo.WebsiteCheckouts.ReconciliationError",
        "dbo.WebsiteCheckouts.ReconciliationCheckedAtUtc"
    };
    missing = missing.Concat(migrationColumns.Where(column => !columns.Contains(column)))
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();

    // Confirm this login cannot mutate mapped application tables. The caller
    // must also verify login grants and active IIS configuration independently.
    foreach (var table in context.Model.GetEntityTypes()
        .Where(entity => entity.GetTableName() is not null)
        .Select(entity => $"{entity.GetSchema() ?? "dbo"}.{entity.GetTableName()}")
        .Distinct(StringComparer.OrdinalIgnoreCase))
    {
        await using var permission = new SqlCommand("""
            SELECT COALESCE(HAS_PERMS_BY_NAME(@table, 'OBJECT', 'INSERT'), 0)
                 + COALESCE(HAS_PERMS_BY_NAME(@table, 'OBJECT', 'UPDATE'), 0)
                 + COALESCE(HAS_PERMS_BY_NAME(@table, 'OBJECT', 'DELETE'), 0)
                 + COALESCE(HAS_PERMS_BY_NAME(@table, 'OBJECT', 'ALTER'), 0)
            """, connection);
        permission.Parameters.AddWithValue("@table", table);
        if (Convert.ToInt32(await permission.ExecuteScalarAsync()) != 0)
            throw new InvalidOperationException("SQL probe login has application-table write privileges");
    }

    var indexPresent = false;
    await using (var command = new SqlCommand("""
        SELECT COUNT(*) FROM sys.indexes i
        WHERE i.object_id = OBJECT_ID(N'dbo.Payments', N'U')
          AND i.name = N'UX_Payments_StripeCheckout_Reference'
          AND i.is_unique = 1 AND i.filter_definition LIKE N'%StripeCheckout%'
        """, connection))
        indexPresent = Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    await using (var history = new SqlCommand("SELECT OBJECT_ID(N'dbo.AniseSchemaMigrations', N'U')", connection))
        if (await history.ExecuteScalarAsync() != DBNull.Value)
            throw new InvalidOperationException("Migration history requires reviewed checksum comparison");

    var result = new
    {
        database,
        server,
        connected = true,
        permission_restricted = true,
        schema_compatible = missing.Length == 0 && indexPresent,
        missing_columns = missing,
        missing_required_index = !indexPresent
    };
    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(result));
    Console.WriteLine("Read-only SQL identity and mapped schema probe completed; compatible: " + result.schema_compatible);
    if (!result.schema_compatible) Environment.ExitCode = 2;
}
catch (Exception error)
{
    // SqlClient exception text can contain connection details; never print it.
    Console.Error.WriteLine("Read-only SQL probe failed: " + error.GetType().Name);
    Environment.ExitCode = 1;
}

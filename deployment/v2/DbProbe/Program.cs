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
    Console.WriteLine("Mapped SQL columns: " + MappedColumns(offlineContext).Length);
    return;
}
if (args.Length != 2 || args[0] != "--output")
    throw new InvalidOperationException("Usage: DbProbe --output result.json | --check-model");

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
    var required = MappedColumns(context);
    var missing = required.Where(column => !columns.Contains(column)).OrderBy(x => x).ToArray();

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
        SELECT COUNT(*) FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Payments', N'U')
          AND name = N'UX_Payments_StripeCheckout_Reference'
        """, connection))
        indexPresent = Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;

    var result = new
    {
        database,
        server,
        connected = true,
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

static string[] MappedColumns(AniseDbContext context)
{
    var columns = context.Model.GetEntityTypes()
        .Where(entity => entity.GetTableName() is not null)
        .SelectMany(entity =>
        {
            var table = entity.GetTableName()!;
            var schema = entity.GetSchema() ?? "dbo";
            // EF's store identifier must retain the model's null default schema.
            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            return entity.GetProperties().Select(property =>
            {
                var column = property.GetColumnName(store);
                if (string.IsNullOrEmpty(column))
                    throw new InvalidOperationException("EF mapped column name unavailable");
                return $"{schema}.{table}.{column}";
            });
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if (columns.Length == 0) throw new InvalidOperationException("No EF mapped columns found");
    return columns;
}

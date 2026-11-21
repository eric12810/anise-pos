using System.Data;
using Microsoft.Data.SqlClient;

namespace AnisePOS.Api.Notifications;

// Shared through the existing POS SQL Server so separate IIS workers see the same short-lived events.
public sealed class CallerEventStore(IConfiguration configuration)
{
    private SqlConnection OpenConnection() => new(configuration.GetConnectionString("AniseDatabase"));

    public async Task<bool> InsertAsync(CallerNotification item, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dbo.CallerEvents (EventId, EventAtUtc, ReceivedAtUtc, Phone, CustomerId, CustomerName)
            VALUES (@eventId, @eventAt, SYSUTCDATETIME(), @phone, @customerId, @customerName)
            """;
        command.Parameters.Add("@eventId", SqlDbType.UniqueIdentifier).Value = item.EventId;
        command.Parameters.Add("@eventAt", SqlDbType.DateTimeOffset).Value = item.Timestamp;
        command.Parameters.Add("@phone", SqlDbType.NVarChar, 21).Value = (object?)item.CallerNumber ?? DBNull.Value;
        command.Parameters.Add("@customerId", SqlDbType.UniqueIdentifier).Value = (object?)item.CustomerId ?? DBNull.Value;
        command.Parameters.Add("@customerName", SqlDbType.NVarChar, 160).Value = (object?)item.CustomerName ?? DBNull.Value;
        try { await command.ExecuteNonQueryAsync(ct); }
        catch (SqlException ex) when (ex.Number is 2601 or 2627) { return false; }
        await using var cleanup = connection.CreateCommand();
        cleanup.CommandText = "DELETE FROM dbo.CallerEvents WHERE ReceivedAtUtc < DATEADD(minute, -10, SYSUTCDATETIME())";
        await cleanup.ExecuteNonQueryAsync(ct);
        return true;
    }

    public async Task<long> LatestSequenceAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Sequence), 0) FROM dbo.CallerEvents";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<StoredCallerEvent>> ReadAfterAsync(long sequence, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (100) Sequence, EventId, EventAtUtc, Phone, CustomerId, CustomerName
            FROM dbo.CallerEvents
            WHERE Sequence > @sequence AND ReceivedAtUtc >= DATEADD(minute, -2, SYSUTCDATETIME())
            ORDER BY Sequence
            """;
        command.Parameters.Add("@sequence", SqlDbType.BigInt).Value = sequence;
        var events = new List<StoredCallerEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            events.Add(new StoredCallerEvent(reader.GetInt64(0), new CallerNotification(
                reader.GetGuid(1), reader.GetDateTimeOffset(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : reader.GetString(5))));
        return events;
    }

    public async Task DeleteExpiredAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.CallerEvents WHERE ReceivedAtUtc < DATEADD(minute, -10, SYSUTCDATETIME())";
        await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed record StoredCallerEvent(long Sequence, CallerNotification Notification);

public sealed class CallerEventCleanupWorker(CallerEventStore store, IConfiguration config, ILogger<CallerEventCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (config["CallerId:IngestSecret"]?.Length is not >= 32) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await store.DeleteExpiredAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Caller event cleanup failed."); }
        }
    }
}

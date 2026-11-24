using System.Security.Cryptography;
using AnisePOS.Api.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AnisePOS.Api.Data;

public static class OrderNumbers
{
    public const int MaximumAttempts = 12;

    public static long Generate() => RandomNumberGenerator.GetInt32(100000, 1000000);

    // SaveChanges uses an EF savepoint when the caller already owns a transaction.
    // The unique constraint is authoritative even when two requests draw the same number.
    public static async Task<bool> SaveNewOrderAsync(AniseDbContext db, Order order, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            order.OrderNumber = Generate();
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException ex) when (IsOrderNumberConflict(ex))
            {
                // EF keeps Added entities tracked after a failed save; retry with a fresh number.
            }
        }
        return false;
    }

    private static bool IsOrderNumberConflict(DbUpdateException ex) =>
        ex.InnerException is SqlException sql && sql.Number is 2601 or 2627 &&
        (sql.Message.Contains("UQ_Orders_OrderNumber", StringComparison.OrdinalIgnoreCase) ||
         sql.Message.Contains("IX_Orders_OrderNumber", StringComparison.OrdinalIgnoreCase));
}

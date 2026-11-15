using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AnisePOS.Api.Data;

public static class ProductionSchemaRequirements
{
    public static string[] MappedColumns(IModel model)
    {
        var columns = model.GetEntityTypes()
            .Where(entity => entity.GetTableName() is not null)
            .SelectMany(entity =>
            {
                var table = entity.GetTableName()!;
                var schema = entity.GetSchema() ?? "dbo";
                // The EF store identifier uses the model schema, which may be null.
                var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
                return entity.GetProperties().Select(property =>
                {
                    var column = property.GetColumnName(store);
                    if (string.IsNullOrEmpty(column))
                        throw new InvalidOperationException("EF mapped column name unavailable");
                    return $"{schema}.{table}.{column}";
                });
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (columns.Length == 0)
            throw new InvalidOperationException("No EF mapped columns found");
        return columns;
    }
}

using AdsMigrator.Migration;
using AdsMigrator.Source;
using static AdsMigrator.Mapping.TypeConverters;

namespace AdsMigrator.Mapping;

// How one table moves: which columns to read, which to write, and how to transform a row.
// Table and key names are the same in source and target.
public sealed record TableMap(
    string Name,
    string KeyColumn,
    string[] SourceColumns,
    string[] TargetColumns,
    Func<SourceRow, MigrationContext, object?[]> Transform);

// Explicit, hand-written maps instead of reading the schema at runtime.
// With a proprietary source you want every column's fate to be a visible, reviewable decision.
public static class TableMaps
{
    // Migration order = dependency order. Parents first, so children can check their parents.
    public static readonly TableMap[] All = { Customers(), Products(), Orders(), OrderItems() };

    private static TableMap Customers() => new(
        Name: "customers",
        KeyColumn: "cust_id",
        SourceColumns: new[] { "cust_id", "cust_code", "name", "email", "phone",
                               "is_active", "created_on", "credit_limit", "notes" },
        TargetColumns: new[] { "cust_id", "cust_code", "name", "email", "phone",
                               "is_active", "created_on", "credit_limit", "notes" },
        Transform: (row, ctx) => new object?[]
        {
            row.Long("cust_id"),
            RequiredText(row.Text("cust_code"), "cust_code"),
            RequiredText(row.Text("name"), "name"),
            TrimChar(row.Text("email")),
            TrimChar(row.Text("phone")),
            ToBool(row.Text("is_active"), "is_active"),
            OptionalDate(row, "created_on", ctx),
            ToMoney(row.Double("credit_limit"), "credit_limit"),
            row.Text("notes") // Memo: kept as-is, NULL stays NULL
        });

    private static TableMap Products() => new(
        Name: "products",
        KeyColumn: "prod_id",
        SourceColumns: new[] { "prod_id", "sku", "description", "unit_price", "discontinued" },
        TargetColumns: new[] { "prod_id", "sku", "description", "unit_price", "discontinued" },
        Transform: (row, ctx) => new object?[]
        {
            row.Long("prod_id"),
            RequiredText(row.Text("sku"), "sku"),
            RequiredText(row.Text("description"), "description"),
            ToMoney(row.Double("unit_price"), "unit_price"),
            ToBool(row.Text("discontinued"), "discontinued")
        });

    private static TableMap Orders() => new(
        Name: "orders",
        KeyColumn: "order_id",
        SourceColumns: new[] { "order_id", "cust_id", "order_date", "ship_date", "status", "total" },
        TargetColumns: new[] { "order_id", "cust_id", "order_date", "ship_date", "status", "total" },
        Transform: (row, ctx) =>
        {
            var custId = row.Long("cust_id");
            ctx.RequireLoaded("customers", custId, "cust_id"); // orphan check

            var status = RequiredText(row.Text("status"), "status");
            if (status is not ("O" or "S" or "C"))
                throw new RowRejectedException($"status: '{status}' is not O, S or C");

            return new object?[]
            {
                row.Long("order_id"),
                custId,
                RequiredDate(row.Text("order_date"), "order_date"),
                OptionalDate(row, "ship_date", ctx),
                status,
                ToMoney(row.Double("total"), "total")
            };
        });

    private static TableMap OrderItems() => new(
        Name: "order_items",
        KeyColumn: "item_id",
        SourceColumns: new[] { "item_id", "order_id", "prod_id", "qty", "unit_price", "line_total" },
        TargetColumns: new[] { "item_id", "order_id", "prod_id", "qty", "unit_price", "line_total" },
        Transform: (row, ctx) =>
        {
            // Checking against what was actually migrated (not what exists in the source)
            // means a rejected order automatically takes its line items with it.
            var orderId = row.Long("order_id");
            var prodId = row.Long("prod_id");
            ctx.RequireLoaded("orders", orderId, "order_id");
            ctx.RequireLoaded("products", prodId, "prod_id");

            var qty = row.Long("qty");
            if (qty <= 0)
                throw new RowRejectedException($"qty: {qty} must be greater than 0");

            return new object?[]
            {
                row.Long("item_id"),
                orderId,
                prodId,
                qty,
                ToMoney(row.Double("unit_price"), "unit_price"),
                ToMoney(row.Double("line_total"), "line_total")
            };
        });

    // Nullable date column: an unparseable value becomes NULL and gets a warning in rejects.csv
    private static DateTime? OptionalDate(SourceRow row, string column, MigrationContext ctx)
    {
        var raw = row.Text(column);
        var date = ParseLegacyDate(raw, out var wasInvalid);
        if (wasInvalid)
            ctx.Warn($"{column}: '{raw}' is not a valid date, migrated as NULL");
        return date;
    }
}

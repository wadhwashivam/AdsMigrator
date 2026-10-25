using Bogus;
using Microsoft.Data.Sqlite;

namespace AdsMigrator.Seeding;

// Row shapes for the fake Advantage-style source.
// Everything is stored the way a legacy Advantage export would look:
// padded CHAR fields, 'T'/'F' logicals, dates as text, money as floating point.
public record Customer(int Id, string Code, string Name, string? Email, string? Phone,
                       string IsActive, string CreatedOn, double CreditLimit, string? Notes);

public record Product(int Id, string Sku, string Description, double UnitPrice, string Discontinued);

public record Order(int Id, int CustId, string OrderDate, string? ShipDate, string Status, double Total);

public record OrderItem(int Id, int OrderId, int ProdId, int Qty, double UnitPrice, double LineTotal);

public class SourceSeeder
{
    private const int CustomerCount = 1000;
    private const int ProductCount = 200;
    private const int OrderCount = 5000;

    // Dates that a real legacy system tends to accumulate
    private static readonly string[] BadDates = { "", "0000-00-00", "2019-02-31", "31/12/2018" };

    private readonly string _connectionString;
    private readonly Faker _f;

    public SourceSeeder(string connectionString)
    {
        _connectionString = connectionString;

        // Fixed seed: identical data on every run, so verification results are repeatable
        Randomizer.Seed = new Random(42);
        _f = new Faker("en");
    }

    public void Run()
    {
        ResetDatabaseFile();

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        CreateSchema(conn);

        var customers = GenerateCustomers();
        var products = GenerateProducts();
        var (orders, items) = GenerateOrdersAndItems(products);

        // Dependency order: parents before children
        BulkInsert(conn, "customers",
            new[] { "cust_id", "cust_code", "name", "email", "phone", "is_active", "created_on", "credit_limit", "notes" },
            customers,
            c => new object?[] { c.Id, c.Code, c.Name, c.Email, c.Phone, c.IsActive, c.CreatedOn, c.CreditLimit, c.Notes });

        BulkInsert(conn, "products",
            new[] { "prod_id", "sku", "description", "unit_price", "discontinued" },
            products,
            p => new object?[] { p.Id, p.Sku, p.Description, p.UnitPrice, p.Discontinued });

        BulkInsert(conn, "orders",
            new[] { "order_id", "cust_id", "order_date", "ship_date", "status", "total" },
            orders,
            o => new object?[] { o.Id, o.CustId, o.OrderDate, o.ShipDate, o.Status, o.Total });

        BulkInsert(conn, "order_items",
            new[] { "item_id", "order_id", "prod_id", "qty", "unit_price", "line_total" },
            items,
            i => new object?[] { i.Id, i.OrderId, i.ProdId, i.Qty, i.UnitPrice, i.LineTotal });

        PrintSummary(conn);
    }

    // ---------- Setup ----------

    private void ResetDatabaseFile()
    {
        var path = new SqliteConnectionStringBuilder(_connectionString).DataSource;

        // Pooled connections keep the file locked; release them before deleting
        SqliteConnection.ClearAllPools();

        if (File.Exists(path))
        {
            File.Delete(path);
            Console.WriteLine($"Deleted existing {path}");
        }
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "sql", "source_schema.sql");
        var sql = File.ReadAllText(schemaPath);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql; // Microsoft.Data.Sqlite runs multiple statements in one command
        cmd.ExecuteNonQuery();

        Console.WriteLine("Source schema created");
    }

    // ---------- Data generation ----------

    private List<Customer> GenerateCustomers()
    {
        var list = new List<Customer>(CustomerCount);

        for (int id = 1; id <= CustomerCount; id++)
        {
            var createdOn = _f.Random.Bool(0.02f)
                ? _f.PickRandom(BadDates)
                : _f.Date.Between(new DateTime(2005, 1, 1), new DateTime(2020, 12, 31)).ToString("yyyy-MM-dd");

            // About 40% NULL notes; some long ones to mimic Memo fields
            string? notes = null;
            if (_f.Random.Bool(0.6f))
                notes = _f.Random.Bool(0.2f) ? _f.Lorem.Paragraphs(3) : _f.Lorem.Sentence();

            list.Add(new Customer(
                Id: id,
                Code: $"C{id:D5}".PadRight(10),
                Name: Fit(_f.Company.CompanyName(), 50),
                Email: _f.Random.Bool(0.95f) ? _f.Internet.Email() : null,
                Phone: _f.Random.Bool(0.9f) ? _f.Phone.PhoneNumber("###-###-####") : null,
                IsActive: _f.Random.Bool(0.85f) ? "T" : "F",
                CreatedOn: createdOn,
                CreditLimit: Math.Round(_f.Random.Double(500, 50000), 2),
                Notes: notes));
        }

        return list;
    }

    private List<Product> GenerateProducts()
    {
        var list = new List<Product>(ProductCount);

        for (int id = 1; id <= ProductCount; id++)
        {
            list.Add(new Product(
                Id: id,
                Sku: $"SKU-{id:D6}".PadRight(12),
                Description: Fit(_f.Commerce.ProductName(), 60),
                UnitPrice: Math.Round(_f.Random.Double(1, 500), 2),
                Discontinued: _f.Random.Bool(0.1f) ? "T" : "F"));
        }

        return list;
    }

    private (List<Order>, List<OrderItem>) GenerateOrdersAndItems(List<Product> products)
    {
        var priceById = products.ToDictionary(p => p.Id, p => p.UnitPrice);
        var orders = new List<Order>(OrderCount);
        var items = new List<OrderItem>(OrderCount * 3);
        var itemId = 1;

        for (int orderId = 1; orderId <= OrderCount; orderId++)
        {
            // About 0.5% of orders point to a customer that doesn't exist
            var custId = _f.Random.Bool(0.005f)
                ? _f.Random.Int(90000, 99999)
                : _f.Random.Int(1, CustomerCount);

            var orderDate = _f.Date.Between(new DateTime(2015, 1, 1), new DateTime(2024, 12, 31));
            var status = _f.Random.WeightedRandom(new[] { "S", "O", "C" }, new[] { 0.80f, 0.15f, 0.05f });

            string? shipDate = status switch
            {
                "S" => _f.Random.Bool(0.02f)
                        ? _f.PickRandom("", "0000-00-00")
                        : orderDate.AddDays(_f.Random.Int(1, 14)).ToString("yyyy-MM-dd"),
                _ => null // open and cancelled orders were never shipped
            };

            // Line items: total is a running sum of doubles, so float noise
            // like 149.97000000000003 shows up naturally (the DECIMAL story)
            double total = 0;
            var lineCount = _f.Random.Int(1, 5);

            for (int n = 0; n < lineCount; n++)
            {
                // A handful of items point to a product that doesn't exist
                var prodId = _f.Random.Bool(0.002f)
                    ? _f.Random.Int(9000, 9999)
                    : _f.Random.Int(1, ProductCount);

                var unitPrice = priceById.TryGetValue(prodId, out var price)
                    ? price
                    : Math.Round(_f.Random.Double(1, 500), 2);

                var qty = _f.Random.Int(1, 10);
                var lineTotal = qty * unitPrice;
                total += lineTotal;

                items.Add(new OrderItem(itemId++, orderId, prodId, qty, unitPrice, lineTotal));
            }

            orders.Add(new Order(orderId, custId, orderDate.ToString("yyyy-MM-dd"), shipDate, status, total));
        }

        return (orders, items);
    }

    // ---------- Insert ----------

    // One transaction per table, one reused command, only parameter values change per row.
    // Without the explicit transaction SQLite commits after every insert and this takes minutes.
    private static void BulkInsert<T>(SqliteConnection conn, string table, string[] columns,
                                      IEnumerable<T> rows, Func<T, object?[]> toValues)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"INSERT INTO {table} ({string.Join(", ", columns)}) " +
            $"VALUES ({string.Join(", ", columns.Select(c => "$" + c))})";

        var parameters = columns.Select(c =>
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "$" + c;
            cmd.Parameters.Add(p);
            return p;
        }).ToArray();

        var count = 0;
        foreach (var row in rows)
        {
            var values = toValues(row);
            for (int i = 0; i < parameters.Length; i++)
                parameters[i].Value = values[i] ?? DBNull.Value; // C# null throws; DB nulls need DBNull

            cmd.ExecuteNonQuery();
            count++;
        }

        tx.Commit();
        Console.WriteLine($"Inserted {count,6} rows into {table}");
    }

    // ---------- Helpers ----------

    // Truncate then space-pad, like an Advantage CHAR(n) column
    private static string Fit(string value, int width) =>
        (value.Length > width ? value[..width] : value).PadRight(width);

    private static void PrintSummary(SqliteConnection conn)
    {
        Console.WriteLine();
        Console.WriteLine("Source summary:");
        foreach (var table in new[] { "customers", "products", "orders", "order_items" })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            Console.WriteLine($"  {table,-12} {cmd.ExecuteScalar()}");
        }
    }
}

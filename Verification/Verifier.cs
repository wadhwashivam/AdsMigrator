using System.Globalization;
using AdsMigrator.Mapping;
using AdsMigrator.Migration;
using AdsMigrator.Source;
using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace AdsMigrator.Verification;

// Proves the migration is correct, independently of what the migrator printed.
// Five checks, each PASS/FAIL. Exit code 0 only if everything passes.
public sealed class Verifier
{
    private const int SpotCheckRows = 10;

    // Money columns to checksum, per table
    private static readonly Dictionary<string, string[]> MoneyColumns = new()
    {
        ["customers"] = new[] { "credit_limit" },
        ["products"] = new[] { "unit_price" },
        ["orders"] = new[] { "total" },
        ["order_items"] = new[] { "unit_price", "line_total" },
    };

    // Every foreign key in the target, as (child table, child column, parent table, parent key)
    private static readonly (string Child, string Column, string Parent, string ParentKey)[] ForeignKeys =
    {
        ("orders", "cust_id", "customers", "cust_id"),
        ("order_items", "order_id", "orders", "order_id"),
        ("order_items", "prod_id", "products", "prod_id"),
    };

    private readonly string _sourceCs;
    private readonly string _targetCs;
    private readonly string _rejectsPath;
    private int _passed;
    private int _failed;

    public Verifier(string sourceCs, string targetCs, string rejectsPath = "rejects.csv")
    {
        _sourceCs = sourceCs;
        _targetCs = targetCs;
        _rejectsPath = rejectsPath;
    }

    public int Run()
    {
        if (!File.Exists(_rejectsPath))
        {
            Console.WriteLine($"{_rejectsPath} not found. Run migrate first, from this same folder.");
            return 1;
        }

        using var source = new SqliteConnection(_sourceCs);
        using var target = new MySqlConnection(_targetCs);
        source.Open();
        target.Open();

        var rejected = LoadRejectedKeys();

        CheckRowCounts(source, target, rejected);
        CheckMoneyChecksums(source, target, rejected);
        CheckOrphans(target);
        CheckAutoIncrement(target);
        SpotCheck(source, target);

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // ---------- 1. Every source row is accounted for ----------

    private void CheckRowCounts(SqliteConnection source, MySqlConnection target,
                                Dictionary<string, HashSet<long>> rejected)
    {
        Section("1. Row counts (source = target + rejected)");

        foreach (var map in TableMaps.All)
        {
            var s = SourceScalarLong(source, $"SELECT COUNT(*) FROM {map.Name}");
            var t = TargetScalarLong(target, $"SELECT COUNT(*) FROM {map.Name}");
            var r = rejected[map.Name].Count;

            Check(map.Name, s == t + r, $"source {s}, target {t}, rejected {r}");
        }
    }

    // ---------- 2. Money adds up to the cent ----------

    // Expected = source values for the rows that were NOT rejected, put through the same
    // rounding the migrator uses. Actual = SUM() in MySQL. They must match exactly.
    private void CheckMoneyChecksums(SqliteConnection source, MySqlConnection target,
                                     Dictionary<string, HashSet<long>> rejected)
    {
        Section("2. Money checksums");

        foreach (var map in TableMaps.All)
        {
            foreach (var column in MoneyColumns[map.Name])
            {
                decimal expected = 0;

                using (var cmd = source.CreateCommand())
                {
                    cmd.CommandText = $"SELECT {map.KeyColumn}, {column} FROM {map.Name}";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        if (rejected[map.Name].Contains(reader.GetInt64(0)) || reader.IsDBNull(1))
                            continue;
                        expected += TypeConverters.ToMoney(reader.GetDouble(1), column);
                    }
                }

                var actual = Convert.ToDecimal(
                    TargetScalar(target, $"SELECT COALESCE(SUM({column}), 0) FROM {map.Name}"),
                    CultureInfo.InvariantCulture);

                Check($"{map.Name}.{column}", expected == actual, $"expected {expected:N2}, target {actual:N2}");
            }
        }
    }

    // ---------- 3. No orphans made it in ----------

    // The foreign keys should make this impossible. Checking anyway is the point:
    // verification shouldn't trust the thing it's verifying.
    private void CheckOrphans(MySqlConnection target)
    {
        Section("3. Orphans in target");

        foreach (var fk in ForeignKeys)
        {
            var orphans = TargetScalarLong(target,
                $"SELECT COUNT(*) FROM {fk.Child} c " +
                $"LEFT JOIN {fk.Parent} p ON c.{fk.Column} = p.{fk.ParentKey} " +
                $"WHERE p.{fk.ParentKey} IS NULL");

            Check($"{fk.Child}.{fk.Column}", orphans == 0, $"{orphans} rows with no {fk.Parent}");
        }
    }

    // ---------- 4. New rows won't collide with migrated IDs ----------

    private void CheckAutoIncrement(MySqlConnection target)
    {
        Section("4. AUTO_INCREMENT past highest migrated ID");

        // MySQL 8 caches information_schema table stats for up to 24 hours by default,
        // which can show a stale AUTO_INCREMENT. Turn the cache off for this session.
        using (var cmd = target.CreateCommand())
        {
            cmd.CommandText = "SET SESSION information_schema_stats_expiry = 0";
            cmd.ExecuteNonQuery();
        }

        foreach (var map in TableMaps.All)
        {
            var maxId = TargetScalarLong(target, $"SELECT COALESCE(MAX({map.KeyColumn}), 0) FROM {map.Name}");

            long next;
            using (var cmd = target.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT AUTO_INCREMENT FROM information_schema.TABLES " +
                    "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table";
                cmd.Parameters.AddWithValue("@table", map.Name);
                var value = cmd.ExecuteScalar();
                next = value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }

            Check(map.Name, next > maxId, $"max id {maxId}, next id {next}");
        }
    }

    // ---------- 5. Field-by-field comparison of random rows ----------

    // Pick random migrated rows, re-run the source row through the same table map,
    // and compare every column with what's actually stored in MySQL.
    private void SpotCheck(SqliteConnection source, MySqlConnection target)
    {
        Section($"5. Spot check ({SpotCheckRows} random rows per table)");

        // The table maps need a context. Seed it with what's in the target so the orphan
        // checks pass, and send its warnings to a throwaway file so rejects.csv isn't touched.
        using var scratchLog = new RejectLog(Path.Combine(Path.GetTempPath(), "adsmigrator-verify-warnings.csv"));
        var ctx = new MigrationContext(scratchLog);

        foreach (var map in TableMaps.All)
            foreach (var key in TargetKeys(target, map, randomLimit: null))
                ctx.MarkLoaded(map.Name, key);

        foreach (var map in TableMaps.All)
        {
            var mismatches = new List<string>();
            var keys = TargetKeys(target, map, randomLimit: SpotCheckRows);

            foreach (var key in keys)
            {
                ctx.BeginRow(map.Name, key);
                var actual = ReadTargetRow(target, map, key);

                object?[] expected;
                try
                {
                    expected = map.Transform(ReadSourceRow(source, map, key), ctx);
                }
                catch (RowRejectedException ex)
                {
                    mismatches.Add($"id {key}: migrated, but source row now rejects ({ex.Message})");
                    continue;
                }

                for (int c = 0; c < map.TargetColumns.Length; c++)
                {
                    var e = Normalize(expected[c]);
                    var a = Normalize(actual[c]);
                    if (e != a)
                        mismatches.Add($"id {key}: {map.TargetColumns[c]} expected '{e}', target '{a}'");
                }
            }

            Check(map.Name, mismatches.Count == 0,
                  mismatches.Count == 0 ? $"{keys.Count} rows match" : $"{mismatches.Count} mismatches");

            foreach (var m in mismatches.Take(5))
                Console.WriteLine($"         {m}");
        }
    }

    // Puts both sides in the same shape before comparing: e.g. 149.97 (C#) vs 149.9700 (MySQL),
    // true (C#) vs a TINYINT(1) read back as bool, a long vs an INT read back as int.
    private static string Normalize(object? value) => value switch
    {
        null or DBNull => "NULL",
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        decimal m => m.ToString("0.00##", CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong =>
            Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL"
    };

    // ---------- Reading helpers ----------

    private Dictionary<string, HashSet<long>> LoadRejectedKeys()
    {
        var rejected = TableMaps.All.ToDictionary(m => m.Name, _ => new HashSet<long>());

        // kind, table and key are never quoted, so a plain split on the first commas is safe
        foreach (var line in File.ReadLines(_rejectsPath).Skip(1))
        {
            var parts = line.Split(',', 4);
            if (parts.Length < 3 || parts[0] != "REJECT")
                continue;

            if (rejected.TryGetValue(parts[1], out var keys) && long.TryParse(parts[2], out var key))
                keys.Add(key);
        }

        return rejected;
    }

    private static List<long> TargetKeys(MySqlConnection target, TableMap map, int? randomLimit)
    {
        var keys = new List<long>();
        using var cmd = target.CreateCommand();
        cmd.CommandText = randomLimit is int n
            ? $"SELECT {map.KeyColumn} FROM {map.Name} ORDER BY RAND() LIMIT {n}"
            : $"SELECT {map.KeyColumn} FROM {map.Name}";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            keys.Add(reader.GetInt64(0));
        return keys;
    }

    private static SourceRow ReadSourceRow(SqliteConnection source, TableMap map, long key)
    {
        using var cmd = source.CreateCommand();
        cmd.CommandText =
            $"SELECT {string.Join(", ", map.SourceColumns)} FROM {map.Name} WHERE {map.KeyColumn} = $key";
        cmd.Parameters.AddWithValue("$key", key);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new RowRejectedException($"not found in source");

        var values = new Dictionary<string, object?>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
            values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return new SourceRow(values);
    }

    private static object?[] ReadTargetRow(MySqlConnection target, TableMap map, long key)
    {
        using var cmd = target.CreateCommand();
        cmd.CommandText =
            $"SELECT {string.Join(", ", map.TargetColumns)} FROM {map.Name} WHERE {map.KeyColumn} = @key";
        cmd.Parameters.AddWithValue("@key", key);

        using var reader = cmd.ExecuteReader();
        reader.Read();

        var values = new object?[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
            values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return values;
    }

    private static long SourceScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static object? TargetScalar(MySqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static long TargetScalarLong(MySqlConnection conn, string sql) =>
        Convert.ToInt64(TargetScalar(conn, sql), CultureInfo.InvariantCulture);

    // ---------- Output ----------

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
    }

    private void Check(string label, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label,-26} {detail}");
        if (ok) _passed++; else _failed++;
    }
}

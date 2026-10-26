using System.Text;
using MySqlConnector;

namespace AdsMigrator.Target;

public sealed class MySqlWriter : IDisposable
{
    private readonly MySqlConnection _conn;

    public MySqlWriter(string connectionString)
    {
        _conn = new MySqlConnection(connectionString);
        _conn.Open();
    }

    public long CountRows(string table)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // Used by --fresh. DELETE children before parents so foreign keys are never violated.
    // (TRUNCATE isn't allowed on a table that other tables reference.)
    public void ClearTables(IEnumerable<string> tablesChildFirst)
    {
        foreach (var table in tablesChildFirst)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table}";
            var deleted = cmd.ExecuteNonQuery();
            Console.WriteLine($"Cleared {deleted} rows from {table}");
        }
    }

    // Writes one batch and returns the rows that failed (index into `rows`, error message).
    //
    // Fast path: the whole batch as ONE multi-row INSERT inside a transaction.
    // A single INSERT is atomic, so if any row breaks a constraint nothing is written.
    //
    // Slow path (only when the fast path fails): roll back, then insert row by row in a
    // new transaction. InnoDB undoes just the failing statement, so good rows still commit
    // and each bad row is identified precisely. Bad batches are rare, so the cost is rare.
    public List<(int Index, string Error)> WriteBatch(string table, string[] columns,
                                                      IReadOnlyList<object?[]> rows)
    {
        var failures = new List<(int Index, string Error)>();
        if (rows.Count == 0)
            return failures;

        using (var tx = _conn.BeginTransaction())
        {
            try
            {
                using var cmd = BuildInsert(table, columns, rows, tx);
                cmd.ExecuteNonQuery();
                tx.Commit();
                return failures;
            }
            catch (MySqlException)
            {
                tx.Rollback();
            }
        }

        using (var tx = _conn.BeginTransaction())
        {
            for (int i = 0; i < rows.Count; i++)
            {
                try
                {
                    using var cmd = BuildInsert(table, columns, new[] { rows[i] }, tx);
                    cmd.ExecuteNonQuery();
                }
                catch (MySqlException ex)
                {
                    failures.Add((i, ex.Message));
                }
            }
            tx.Commit();
        }

        return failures;
    }

    // INSERT INTO t (a, b) VALUES (@p0_0, @p0_1), (@p1_0, @p1_1), ...
    // Always parameterized: values never get concatenated into the SQL string.
    private MySqlCommand BuildInsert(string table, string[] columns,
                                     IReadOnlyList<object?[]> rows, MySqlTransaction tx)
    {
        var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;

        var sql = new StringBuilder();
        sql.Append($"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ");

        for (int r = 0; r < rows.Count; r++)
        {
            if (r > 0) sql.Append(", ");
            sql.Append('(');
            for (int c = 0; c < columns.Length; c++)
            {
                if (c > 0) sql.Append(", ");
                var name = $"@p{r}_{c}";
                sql.Append(name);
                cmd.Parameters.AddWithValue(name, rows[r][c] ?? DBNull.Value);
            }
            sql.Append(')');
        }

        cmd.CommandText = sql.ToString();
        return cmd;
    }

    public void Dispose() => _conn.Dispose();
}

using AdsMigrator.Mapping;
using Microsoft.Data.Sqlite;

namespace AdsMigrator.Source;

// One source row, column name -> value. SQLite hands back long, double, string or NULL.
public sealed class SourceRow
{
    private readonly Dictionary<string, object?> _values;

    public SourceRow(Dictionary<string, object?> values) => _values = values;

    public object? this[string column] => _values[column];

    public string? Text(string column) => _values[column]?.ToString();

    public long Long(string column) => _values[column] switch
    {
        long l => l,
        null => throw new RowRejectedException($"{column} is NULL"),
        var v => throw new RowRejectedException($"{column}: '{v}' is not an integer")
    };

    public double Double(string column) => _values[column] switch
    {
        double d => d,
        long l => l,
        null => throw new RowRejectedException($"{column} is NULL"),
        var v => throw new RowRejectedException($"{column}: '{v}' is not a number")
    };

    // Compact one-line version of the row for rejects.csv (long Memo text is cut short)
    public string Describe() =>
        string.Join("; ", _values.Select(kv =>
        {
            var text = kv.Value?.ToString() ?? "NULL";
            return $"{kv.Key}={(text.Length > 40 ? text[..40] + "..." : text)}";
        }));
}

public sealed class SqliteReader : IDisposable
{
    private readonly SqliteConnection _conn;

    public SqliteReader(string connectionString)
    {
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
    }

    public long CountRows(string table)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // Keyset pagination: "WHERE id > last_id ORDER BY id LIMIT n".
    // Unlike OFFSET, the database jumps straight to the next batch via the primary key
    // instead of scanning and discarding every earlier row, so each batch costs the same.
    public IEnumerable<List<SourceRow>> ReadBatches(string table, string keyColumn,
                                                    string[] columns, int batchSize)
    {
        long lastKey = long.MinValue;

        while (true)
        {
            var batch = new List<SourceRow>(batchSize);

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText =
                    $"SELECT {string.Join(", ", columns)} FROM {table} " +
                    $"WHERE {keyColumn} > $last ORDER BY {keyColumn} LIMIT $limit";
                cmd.Parameters.AddWithValue("$last", lastKey);
                cmd.Parameters.AddWithValue("$limit", batchSize);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var values = new Dictionary<string, object?>(reader.FieldCount);
                    for (int i = 0; i < reader.FieldCount; i++)
                        values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

                    batch.Add(new SourceRow(values));
                }
            }

            if (batch.Count == 0)
                yield break;

            lastKey = (long)batch[^1][keyColumn]!;
            yield return batch;

            if (batch.Count < batchSize)
                yield break; // short batch means we've reached the end
        }
    }

    public void Dispose() => _conn.Dispose();
}

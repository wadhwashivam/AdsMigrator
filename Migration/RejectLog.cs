namespace AdsMigrator.Migration;

// Writes rejects.csv. Two kinds of line:
//   REJECT - the row was NOT migrated (source count = migrated + rejects)
//   WARN   - the row WAS migrated, but a value was changed (e.g. a bad date became NULL)
public sealed class RejectLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Dictionary<string, int> _rejects = new();
    private readonly Dictionary<string, int> _warnings = new();

    public string FilePath { get; }

    public RejectLog(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        _writer = new StreamWriter(FilePath, append: false);
        _writer.WriteLine("kind,table,source_key,reason,source_row");
    }

    public void Reject(string table, long key, string reason, string sourceRow)
    {
        Write("REJECT", table, key, reason, sourceRow);
        _rejects[table] = RejectCount(table) + 1;
    }

    public void Warn(string table, long key, string reason)
    {
        Write("WARN", table, key, reason, "");
        _warnings[table] = WarningCount(table) + 1;
    }

    public int RejectCount(string table) => _rejects.GetValueOrDefault(table);
    public int WarningCount(string table) => _warnings.GetValueOrDefault(table);

    private void Write(string kind, string table, long key, string reason, string sourceRow) =>
        _writer.WriteLine($"{kind},{table},{key},{Escape(reason)},{Escape(sourceRow)}");

    // CSV rule: wrap in quotes if the value has a comma, quote or newline; double any quotes
    private static string Escape(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    public void Dispose() => _writer.Dispose();
}

using AdsMigrator.Mapping;

namespace AdsMigrator.Migration;

// State shared across the run: which keys made it into the target,
// and which row is currently being transformed (so warnings can say where they came from).
public sealed class MigrationContext
{
    private readonly Dictionary<string, HashSet<long>> _loaded = new();
    private readonly RejectLog _log;

    public string CurrentTable { get; private set; } = "";
    public long CurrentKey { get; private set; }

    public MigrationContext(RejectLog log) => _log = log;

    public void BeginRow(string table, long key)
    {
        CurrentTable = table;
        CurrentKey = key;
    }

    public void MarkLoaded(string table, long key)
    {
        if (!_loaded.TryGetValue(table, out var keys))
            _loaded[table] = keys = new HashSet<long>();
        keys.Add(key);
    }

    // Foreign key check done in C# before the insert, so an orphan gets a clear
    // reason in rejects.csv instead of a MySQL constraint error, and never costs a slow batch.
    public void RequireLoaded(string parentTable, long key, string column)
    {
        if (!_loaded.TryGetValue(parentTable, out var keys) || !keys.Contains(key))
            throw new RowRejectedException($"{column}={key} has no migrated row in {parentTable}");
    }

    public void Warn(string message) => _log.Warn(CurrentTable, CurrentKey, message);
}

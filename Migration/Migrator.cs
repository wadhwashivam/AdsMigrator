using System.Diagnostics;
using AdsMigrator.Mapping;
using AdsMigrator.Source;
using AdsMigrator.Target;

namespace AdsMigrator.Migration;

public sealed class Migrator
{
    private readonly string _sourceCs;
    private readonly string _targetCs;
    private readonly int _batchSize;
    private readonly bool _fresh;
    private readonly bool _failFast;

    private sealed record TableResult(string Table, long Read, long Written, int Rejected,
                                      int Warnings, TimeSpan Elapsed);

    private sealed class FailFastException : Exception
    {
        public FailFastException(string message) : base(message) { }
    }

    public Migrator(string sourceCs, string targetCs, int batchSize, bool fresh, bool failFast)
    {
        _sourceCs = sourceCs;
        _targetCs = targetCs;
        _batchSize = batchSize;
        _fresh = fresh;
        _failFast = failFast;
    }

    // Returns a process exit code: 0 = success, 1 = stopped
    public int Run()
    {
        using var reader = new SqliteReader(_sourceCs);
        using var writer = new MySqlWriter(_targetCs);
        using var log = new RejectLog("rejects.csv");
        var ctx = new MigrationContext(log);

        var tablesChildFirst = TableMaps.All.Select(m => m.Name).Reverse().ToArray();

        // Pre-flight: never migrate on top of existing data by accident
        if (_fresh)
        {
            writer.ClearTables(tablesChildFirst);
        }
        else if (tablesChildFirst.Any(t => writer.CountRows(t) > 0))
        {
            Console.WriteLine("Target already has data. Re-run with --fresh to clear it first.");
            return 1;
        }

        Console.WriteLine($"Migrating with batch size {_batchSize}" +
                          (_failFast ? " (fail-fast)" : "") + "\n");

        var total = Stopwatch.StartNew();
        var results = new List<TableResult>();

        try
        {
            foreach (var map in TableMaps.All)
                results.Add(MigrateTable(map, reader, writer, log, ctx));
        }
        catch (FailFastException ex)
        {
            Console.WriteLine($"\nStopped (--fail-fast): {ex.Message}");
            Console.WriteLine("Batches already committed stay in the target. Re-run with --fresh.");
            return 1;
        }

        PrintSummary(results, total.Elapsed, log.FilePath);
        return 0;
    }

    private TableResult MigrateTable(TableMap map, SqliteReader reader, MySqlWriter writer,
                                     RejectLog log, MigrationContext ctx)
    {
        var timer = Stopwatch.StartNew();
        long read = 0, written = 0;

        foreach (var batch in reader.ReadBatches(map.Name, map.KeyColumn, map.SourceColumns, _batchSize))
        {
            // 1. Transform. Rows that fail conversion or the orphan check are rejected here.
            var accepted = new List<(SourceRow Source, long Key, object?[] Values)>(batch.Count);

            foreach (var row in batch)
            {
                read++;
                var key = (long)row[map.KeyColumn]!;
                ctx.BeginRow(map.Name, key);

                try
                {
                    accepted.Add((row, key, map.Transform(row, ctx)));
                }
                catch (RowRejectedException ex)
                {
                    Reject(log, map.Name, key, ex.Message, row);
                }
            }

            // 2. Write. Anything MySQL still refuses (CHECK, UNIQUE, ...) is rejected too.
            var failures = writer.WriteBatch(map.Name, map.TargetColumns,
                                             accepted.Select(a => a.Values).ToList());
            var failedIndexes = new HashSet<int>();

            foreach (var (index, error) in failures)
            {
                failedIndexes.Add(index);
                Reject(log, map.Name, accepted[index].Key, $"MySQL: {error}", accepted[index].Source);
            }

            // 3. Remember what actually landed, so child tables can check against it
            for (int i = 0; i < accepted.Count; i++)
            {
                if (failedIndexes.Contains(i)) continue;
                ctx.MarkLoaded(map.Name, accepted[i].Key);
                written++;
            }

            Console.Write($"\r  {map.Name,-12} {read,7} read, {written,7} written");
        }

        Console.WriteLine();
        return new TableResult(map.Name, read, written, log.RejectCount(map.Name),
                               log.WarningCount(map.Name), timer.Elapsed);
    }

    private void Reject(RejectLog log, string table, long key, string reason, SourceRow row)
    {
        log.Reject(table, key, reason, row.Describe());
        if (_failFast)
            throw new FailFastException($"{table} {key}: {reason}");
    }

    private static void PrintSummary(List<TableResult> results, TimeSpan total, string rejectsPath)
    {
        Console.WriteLine();
        Console.WriteLine($"{"table",-12} {"read",8} {"written",8} {"rejected",9} {"warnings",9} {"time",8}");
        Console.WriteLine(new string('-', 59));

        foreach (var r in results)
        {
            // Every source row must be accounted for: either migrated or rejected
            var check = r.Read == r.Written + r.Rejected ? "" : "  <-- COUNT MISMATCH";
            Console.WriteLine($"{r.Table,-12} {r.Read,8} {r.Written,8} {r.Rejected,9} {r.Warnings,9} " +
                              $"{r.Elapsed.TotalSeconds,7:F2}s{check}");
        }

        Console.WriteLine(new string('-', 59));
        Console.WriteLine($"Total time {total.TotalSeconds:F2}s");
        Console.WriteLine($"Rejects and warnings: {rejectsPath}");
    }
}

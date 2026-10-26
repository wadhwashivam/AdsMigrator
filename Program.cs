using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using AdsMigrator.Seeding;
using AdsMigrator.Migration;
using AdsMigrator.Verification;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var sourceCs = config.GetConnectionString("Source")!;
var targetCs = config.GetConnectionString("Target")!;

var mode = args.FirstOrDefault() ?? "help";
switch(mode)
{
    case "test":
        using (var s = new SqliteConnection(sourceCs)) { s.Open(); Console.WriteLine("SQLite OK"); }
        using (var t = new MySqlConnection(targetCs)) { t.Open(); Console.WriteLine("MySQL OK");}
        break;
    case "seed": new SourceSeeder(sourceCs).Run(); break;
    case "migrate":
        {
            var flags = args.Skip(1).Select(a => a.TrimStart('-')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var batchSize = int.TryParse(config["Migration:BatchSize"], out var size) && size > 0 ? size : 1000;
            Environment.ExitCode = new Migrator(sourceCs, targetCs, batchSize,
                fresh: flags.Contains("fresh"), failFast: flags.Contains("fail-fast")).Run();
            break;
        }
    case "verify":
        Environment.ExitCode = new Verifier(sourceCs, targetCs).Run();
        break;
    default: Console.WriteLine("Usage: seed | migrate | verify | test"); break;
}
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using AdsMigrator.Seeding;

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
    case "migrate": /* Phase 4 */ break;
    case "verify": /* Phase 5 */ break;
    default: Console.WriteLine("Usage: seed | migrate | verify | test"); break;
}
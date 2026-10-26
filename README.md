# AdsMigrator

A C# console app that migrates data from an Advantage Database Server (ADS) database to MySQL, then checks the migration is correct.

This is a rebuild of a real migration I worked on with a friend. I handled the database side. The original moved a client off Advantage, a proprietary database, so they could run MySQL on their new server. I rebuilt it from scratch to show how it works and why it was built this way. It uses generated sample data, not client data.

## The problem

Advantage is proprietary and getting harder to support. The client wanted MySQL on their new server, and their data had the usual legacy problems:

- Text fields padded with trailing spaces (Advantage `CHAR(n)`)
- Booleans stored as `'T'` / `'F'`
- Dates stored as text, some invalid (`0000-00-00`, `2019-02-31`, blanks)
- Money stored as floating point, with noise like `149.97000000000003`
- No foreign key enforcement, so some orders point to customers that don't exist

A straight copy would have moved all of that into the new database. The goal was to clean it on the way in, without silently losing or changing anything.

## Running it

You need the .NET SDK, Docker, and SQLite (already on macOS).

```bash
# 1. Start MySQL
docker run --name mysql-target -e MYSQL_ROOT_PASSWORD=devpass \
  -e MYSQL_DATABASE=target_db -p 3306:3306 -d mysql:8.4

# 2. Config: copy the example and set your password
cp appsettings.example.json appsettings.json

# 3. Create the target schema
docker exec -i mysql-target mysql -uroot -pdevpass target_db < sql/target_schema.sql

# 4. Generate the fake Advantage source (source.db)
dotnet run -- seed

# 5. Migrate
dotnet run -- migrate            # first run
dotnet run -- migrate --fresh    # clear the target and run again
dotnet run -- migrate --fail-fast  # stop at the first bad row

# 6. Verify
dotnet run -- verify
```

Rejected rows and warnings are written to `rejects.csv`.

## How it works

```
SQLite (fake Advantage)  →  read in batches  →  convert + validate  →  write in batches  →  MySQL
                                                       ↓
                                                  rejects.csv
```

The source is SQLite, set up to look like an Advantage export, because a real Advantage server is hard to get locally. The migration logic doesn't depend on that, so swapping in the Advantage .NET provider would only change the reader.

Tables are migrated in dependency order: customers, products, orders, order_items. Parents always go before children.

### Reading

Rows are read 1,000 at a time using keyset pagination:

```sql
SELECT ... FROM orders WHERE order_id > @last ORDER BY order_id LIMIT 1000
```

I used this instead of `OFFSET` because `OFFSET` makes the database scan and throw away every earlier row, so batches get slower the further in you go. Keyset pagination jumps straight to the next batch using the primary key.

### Converting

Each table has an explicit column map in `Mapping/TableMaps.cs`. I wrote these by hand instead of reading the schema at runtime. With a proprietary source, I wanted every column's treatment to be a visible decision someone can review.

The conversions are small pure functions in `Mapping/TypeConverters.cs`, so they're easy to test.

| Advantage | MySQL | Decision |
|---|---|---|
| AutoInc | `INT AUTO_INCREMENT` | Keep the original IDs so relationships stay intact |
| Character (padded) | `VARCHAR` | Trim the padding. Blank becomes NULL, or a reject if the column is required |
| CICharacter | `VARCHAR`, `utf8mb4_0900_ai_ci` | Case-insensitive collation keeps the old comparison behaviour |
| Logical (`T`/`F`) | `TINYINT(1)` | Convert to 1/0. Anything else is rejected |
| Memo | `TEXT` | Copied as-is |
| Money (float) | `DECIMAL(19,4)` | Round to cents in C# before inserting |
| Date (text) | `DATE` | Only `yyyy-MM-dd` is accepted. Other formats are not guessed |

Some decisions worth explaining:

- **Money is rounded explicitly.** `DECIMAL` columns would round on insert anyway, but silently. Doing it in code makes the rule visible and testable. Money is never stored as a float, because floats can't represent most decimal values exactly.
- **Bad dates aren't guessed.** `03/04/2018` could be March or April. Guessing wrong is worse than having no date. On optional columns (`created_on`, `ship_date`) a bad date becomes NULL with a warning. On required columns (`order_date`) the row is rejected.
- **Orphans are rejected, not hidden.** A common shortcut is `SET FOREIGN_KEY_CHECKS = 0`, which lets everything in. That just moves the broken data into the new database. Here, foreign keys stay on and orphans are caught in C# first, so each one gets a clear reason in `rejects.csv`.
- **Rejects cascade.** The orphan check compares against what was actually migrated, not what exists in the source. If an order is rejected, its line items are rejected too, automatically.

### Writing

Each batch is written as one parameterized multi-row `INSERT` inside a transaction.

I picked batches over the alternatives:

- **One transaction for everything:** one bad row would roll back the whole migration, and a huge transaction strains the server.
- **One commit per row:** safe, but very slow.
- **Batches:** fast, and a failure only affects one batch.

If MySQL rejects a batch (for example a constraint the C# checks didn't catch), the batch is rolled back and retried row by row. Good rows still commit, and the bad row is identified exactly. This slow path only runs for batches that fail.

All values go in as parameters, never concatenated into the SQL.

### Safety

- The migrator refuses to run if the target already has data, unless you pass `--fresh`. This stops it from migrating on top of a previous run by accident.
- `--fresh` deletes child tables before parents, so foreign keys are never violated.
- `--fail-fast` stops at the first rejected row, for strict runs.

## Verification

`dotnet run -- verify` checks the result independently of what the migrator reported:

1. **Row counts:** for every table, source = target + rejected. Nothing goes missing.
2. **Money checksums:** totals of every money column must match to the cent.
3. **Orphans:** `LEFT JOIN` queries confirm no row in MySQL points to a missing parent. The foreign keys should already guarantee this, but the verifier doesn't assume it.
4. **AUTO_INCREMENT:** the next ID is past the highest migrated ID, so new rows won't collide with old ones.
5. **Spot check:** 10 random rows per table are compared field by field with their source rows.

One limitation: the spot check uses the same converters as the migrator. It proves rows were written and read back correctly, but it wouldn't catch a conversion rule that's wrong in both places. The count and checksum checks don't have that weakness. Unit tests on the converters would close the gap.

## Project structure

```
Program.cs                    Entry point: seed | migrate | verify | test
sql/source_schema.sql         Fake Advantage schema (no foreign keys, like the original)
sql/target_schema.sql         MySQL schema with foreign keys and CHECK constraints
Seeding/SourceSeeder.cs       Generates messy sample data with Bogus (fixed seed, so it's repeatable)
Source/SqliteReader.cs        Batched reads with keyset pagination
Target/MySqlWriter.cs         Batched writes with transactions and row-by-row fallback
Mapping/TypeConverters.cs     Value conversions
Mapping/TableMaps.cs          Column maps and validation for each table
Migration/Migrator.cs         Runs the migration and prints the summary
Migration/MigrationContext.cs Tracks migrated keys for orphan checks
Migration/RejectLog.cs        Writes rejects.csv
Verification/Verifier.cs      Post-migration checks
```

## What I'd add next

- Unit tests for `TypeConverters`
- Resume support, so a crash halfway through can continue instead of starting over with `--fresh`
- `MySqlBulkCopy` instead of multi-row inserts, with timings to compare the two
- A real Advantage reader using the Advantage .NET Data Provider

## Tech

C# / .NET, MySqlConnector, Microsoft.Data.Sqlite, Bogus, MySQL 8.4 in Docker.

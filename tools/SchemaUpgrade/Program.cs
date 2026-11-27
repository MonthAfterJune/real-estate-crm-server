using System.Diagnostics;
using System.Text.Json;
using CRM.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine("CRM", "appsettings.json")));
var connectionString = Environment.GetEnvironmentVariable("CRM_MIGRATION_CONNECTION")
    ?? settings.RootElement.GetProperty("ConnectionStrings").GetProperty("DefaultConnection").GetString()!;
var mode = args.FirstOrDefault() ?? "check";
await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();

if (mode == "backup")
{
    var target = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/database-backups/pre-simplified-erd.dump");
    var backupRoot = Path.GetFullPath("artifacts/database-backups") + Path.DirectorySeparatorChar;
    if (!target.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Backup must be inside artifacts/database-backups.");
    Directory.CreateDirectory(backupRoot);
    if (File.Exists(target)) throw new IOException("Backup already exists; choose a new filename.");
    var parsed = new NpgsqlConnectionStringBuilder(connectionString);
    var start = new ProcessStartInfo(@"C:\Program Files\PostgreSQL\18\bin\pg_dump.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "--format=custom", "--host", parsed.Host!, "--port", parsed.Port.ToString(), "--username", parsed.Username!, "--dbname", parsed.Database!, "--no-password" }) start.ArgumentList.Add(argument);
    start.Environment["PGPASSWORD"] = parsed.Password;
    start.Environment["PGSSLMODE"] = parsed.SslMode.ToString().ToLowerInvariant();
    using var process = Process.Start(start)!;
    var errors = process.StandardError.ReadToEndAsync();
    await using (var file = File.Create(target)) await process.StandardOutput.BaseStream.CopyToAsync(file);
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException("pg_dump failed: " + await errors);
    Console.WriteLine("Full database backup saved: " + target);
    return;
}

if (mode == "test")
{
    var schema = "erd_verification_" + Guid.NewGuid().ToString("N");
    await Execute(connection, $"CREATE SCHEMA \"{schema}\"");
    try
    {
        var isolated = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(isolated.ConnectionString,
            provider => provider.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options;
        await using var db = new ApplicationDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261010161816_AddPropertySellerUser");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Users" ("Id", "FullName", "Email", "RoleId", "PasswordHash") VALUES
              (1, 'Taylor Agent', 'agent-one@example.com', (SELECT "Id" FROM "Roles" WHERE "Name" = 'agent' LIMIT 1), 'fixture-credential-must-survive'),
              (2, 'Sam Owner', 'seller@example.com', (SELECT "Id" FROM "Roles" WHERE "Name" = 'seller' LIMIT 1), NULL),
              (3, 'Second Agent', 'agent-two@example.com', (SELECT "Id" FROM "Roles" WHERE "Name" = 'agent' LIMIT 1), NULL);
            INSERT INTO "Contacts" ("Id", "FirstName", "LastName", "Email", "Type") VALUES
              (10, 'Sam', 'Owner', 'seller@example.com', 'Seller'), (11, 'Pat', 'Buyer', 'buyer@example.com', 'Buyer'), (12, 'Unassigned', 'Buyer', NULL, 'Buyer');
            INSERT INTO "Properties" ("Id", "Address", "Price", "Status", "Bedrooms", "SellerContactId", "SellerUserId", "ListingAgentId") VALUES
              (10, 'Legacy contact listing', 100, 'Active', NULL, 10, NULL, 1),
              (11, 'Legacy user listing', 200, 'Active', 2, NULL, 2, 1),
              (12, 'Second agent listing', 300, 'Active', 3, NULL, 2, 3);
            INSERT INTO "PipelineStages" ("Id", "Name", "SortOrder") VALUES (1, 'Offer', 1);
            INSERT INTO "Opportunities" ("Id", "BuyerContactId", "PropertyId", "AgentId", "StageId", "OfferAmount") VALUES (30, 11, 11, 1, 1, 190);
            INSERT INTO "Leads" ("Id", "Name", "Source", "OwnerId", "PropertyId", "ConvertedContactId") VALUES
              (20, 'Alex ' || repeat('a', 140), 'Referral', 1, 10, NULL), (21, 'Pat Buyer', 'Website', 3, 11, 11);
            INSERT INTO "Activities" ("Id", "Type", "ContactId", "LeadId", "OpportunityId", "OccurredAt") VALUES
              (40, 'Call', NULL, 20, 30, now()), (41, 'Email', 11, 21, 30, now()), (42, 'Tour', NULL, NULL, 30, now());
            INSERT INTO "Notes" ("Id", "AuthorId", "EntityType", "EntityId", "Body") VALUES (50, 1, 'Lead', 20, 'Original lead note');
            """);
        await migrator.MigrateAsync();
        var sellerProperty = await db.Properties.Include(p => p.SellerContact).SingleAsync(p => p.Id == 11);
        Check(sellerProperty.SellerContactId == 10, "matching seller contact reused");
        Check((await db.Properties.Include(p => p.SellerContact).SingleAsync(p => p.Id == 12)).SellerContact.OwnerId == 3, "seller account mapped per owner");
        Check((await db.Properties.SingleAsync(p => p.Id == 10)).Bedrooms == 0, "unknown bedrooms normalized");
        var prospect = await db.Contacts.SingleAsync(c => c.Type == "Prospect");
        Check(prospect.Source == "Referral" && prospect.OwnerId == 1 && prospect.Status == "New", "unconverted lead migrated");
        Check((await db.Activities.SingleAsync(a => a.Id == 40)).ContactId == prospect.Id, "lead activity remapped");
        Check((await db.Activities.SingleAsync(a => a.Id == 42)).ContactId == 11, "opportunity activity backfilled");
        var originalNote = await db.Notes.SingleAsync(n => n.Id == 50);
        Check(originalNote.EntityType == "Contact" && originalNote.EntityId == prospect.Id && originalNote.Body == "Original lead note", "lead notes remapped");
        var archive = await db.Notes.SingleAsync(n => n.EntityId == prospect.Id && n.Body.Contains("legacyLead"));
        using var archiveJson = JsonDocument.Parse(archive.Body);
        Check(archiveJson.RootElement.GetProperty("legacyLead").GetProperty("Name").GetString()!.Length == 145, "full original lead name archived");
        Check(archiveJson.RootElement.GetProperty("legacyActivities").GetArrayLength() == 1, "original activity references archived");
        Check((await db.Contacts.SingleAsync(c => c.Id == 12)).OwnerId == 1, "unlinked contact owner backfilled");
        Check((await db.Contacts.SingleAsync(c => c.Id == 11)).Status == "Active", "converted contact preserved");
        Check((await db.Users.SingleAsync(u => u.Id == 1)).PasswordHash == "fixture-credential-must-survive", "Identity credentials preserved");
        Check(!db.Database.HasPendingModelChanges(), "migration snapshot matches model");
        await using var isolatedConnection = new NpgsqlConnection(isolated.ConnectionString);
        await isolatedConnection.OpenAsync();
        Check((await Scalar(isolatedConnection, "SELECT to_regclass('\"Leads\"')::text")) is null, "Leads table removed");
        Console.WriteLine("Populated legacy schema upgrade passed.");

        // A second isolated schema verifies transaction rollback on ambiguous old data.
        var guardSchema = schema + "_guard";
        await Execute(connection, $"CREATE SCHEMA \"{guardSchema}\"");
        try
        {
            var guardConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = guardSchema };
            var guardOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(guardConnectionString.ConnectionString,
                provider => provider.MigrationsHistoryTable("__EFMigrationsHistory", guardSchema)).Options;
            await using var guard = new ApplicationDbContext(guardOptions);
            await guard.GetService<IMigrator>().MigrateAsync("20261010161816_AddPropertySellerUser");
            await guard.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Users" ("Id", "FullName", "Email", "RoleId") VALUES (1, 'Agent', 'guard@example.com', (SELECT "Id" FROM "Roles" WHERE "Name" = 'agent' LIMIT 1));
                INSERT INTO "Contacts" ("Id", "FirstName", "LastName", "Type") VALUES (1, 'Sam', 'Owner', 'Seller');
                INSERT INTO "Properties" ("Address", "Price", "Status", "Bedrooms", "SellerContactId", "ListingAgentId") VALUES ('Guard', 100, 'Active', 1, 1, 1);
                INSERT INTO "Activities" ("Type", "ContactId", "OccurredAt") VALUES ('Call', 1, now());
                """);
            try { await guard.Database.MigrateAsync(); throw new Exception("Migration should reject standalone activities."); }
            catch (PostgresException error) when (error.SqlState == "P0001") { }
            await using var guardConnection = new NpgsqlConnection(guardConnectionString.ConnectionString);
            await guardConnection.OpenAsync();
            Check((await Scalar(guardConnection, "SELECT to_regclass('\"Leads\"')::text")) is not null, "guard rollback kept Leads table");
            Check(Convert.ToInt32(await Scalar(guardConnection, "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Contacts' AND column_name = 'OwnerId'")) == 0, "guard rollback reverted schema changes");
            Console.WriteLine("Unresolved activity guard and rollback passed.");
        }
        finally { await Execute(connection, $"DROP SCHEMA \"{guardSchema}\" CASCADE"); }
    }
    finally
    {
        // These schemas are generated above; never delete an arbitrary user-supplied schema.
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^erd_verification_[a-f0-9]{32}$")) throw new Exception("Unsafe cleanup target.");
        await Execute(connection, $"DROP SCHEMA \"{schema}\" CASCADE");
    }
    return;
}

if (mode != "check") throw new ArgumentException("Choose check, backup, or test.");
var oldSchema = (await Scalar(connection, "SELECT to_regclass('\"Leads\"')::text")) is not null;
var sql = oldSchema ? """
    SELECT json_build_object('schema', 'legacy',
      'users', (SELECT count(*) FROM "Users"), 'contacts', (SELECT count(*) FROM "Contacts"),
      'leads', (SELECT count(*) FROM "Leads"), 'properties', (SELECT count(*) FROM "Properties"),
      'sellerUserAssignments', (SELECT count(*) FROM "Properties" WHERE "SellerUserId" IS NOT NULL),
      'propertiesWithoutSeller', (SELECT count(*) FROM "Properties" WHERE "SellerUserId" IS NULL AND "SellerContactId" IS NULL),
      'activitiesWithoutOpportunity', (SELECT count(*) FROM "Activities" WHERE "OpportunityId" IS NULL))::text;
    """ : """
    SELECT json_build_object('schema', 'simplified',
      'users', (SELECT count(*) FROM "Users"), 'contacts', (SELECT count(*) FROM "Contacts"),
      'properties', (SELECT count(*) FROM "Properties"), 'activities', (SELECT count(*) FROM "Activities"),
      'contactsWithoutOwner', (SELECT count(*) FROM "Contacts" WHERE "OwnerId" IS NULL),
      'propertiesWithoutSeller', (SELECT count(*) FROM "Properties" WHERE "SellerContactId" IS NULL))::text;
    """;
Console.WriteLine(await Scalar(connection, sql));

static async Task Execute(NpgsqlConnection connection, string sql) { await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }
static async Task<object?> Scalar(NpgsqlConnection connection, string sql) { await using var command = new NpgsqlCommand(sql, connection); var result = await command.ExecuteScalarAsync(); return result is DBNull ? null : result; }
static void Check(bool condition, string description) { if (!condition) throw new Exception("Verification failed: " + description); Console.WriteLine("PASS: " + description); }


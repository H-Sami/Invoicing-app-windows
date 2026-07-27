#pragma warning disable xUnit1051, CA1869
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MHC.Invoicing.Application.Maintenance;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Infrastructure.Backup;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;


namespace MHC.Invoicing.Infrastructure.Tests.Backup;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task RestoreAsync_RejectsCryptographicallyInvalidCanonicalPdfBeforeReplacement()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await SeedFinalizedInvoiceAsync(fixture.Database);
        BackupService service = new();
        await service.CreateAsync(
            fixture.Database, fixture.Documents, fixture.Package,
            DatabaseInitializer.SchemaVersion, "4.0");
        string target = Path.Combine(fixture.Root, "target.db");
        await CreateDatabaseAsync(target);
        await SetSettingAsync(target, "target", "preserved");

        await RewriteDatabaseEntryAsync(fixture.Package, async database =>
        {
            await using SqliteConnection connection = new($"Data Source={database};Pooling=False");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            string triggerSql = Convert.ToString(await ScalarAsync(connection,
                "SELECT sql FROM sqlite_schema WHERE name='trg_invoice_documents_no_update';"),
                System.Globalization.CultureInfo.InvariantCulture)!;
            await ExecuteAsync(database, "DROP TRIGGER trg_invoice_documents_no_update;");
            await ExecuteAsync(database, "UPDATE invoice_documents SET sha256=zeroblob(32);");
            await ExecuteAsync(database, triggerSql);
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(
            fixture.Package, target, Path.Combine(fixture.Root, "target-documents"),
            DatabaseInitializer.SchemaVersion, true));
        Assert.Equal("preserved", await ReadSettingAsync(target, "target"));
    }

    [Fact]
    public async Task CreateAsync_RejectsEqualAndOverlappingPathsBeforeWriting()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        BackupService service = new();

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            fixture.Database, fixture.Documents, fixture.Database, DatabaseInitializer.SchemaVersion, "4.0"));
        Assert.True(File.Exists(fixture.Database));

        string packageInsideDocuments = Path.Combine(fixture.Documents, "backup.mhcbackup");
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            fixture.Database, fixture.Documents, packageInsideDocuments, DatabaseInitializer.SchemaVersion, "4.0"));
        Assert.False(File.Exists(packageInsideDocuments));
    }

    [Fact]
    public async Task CreateAsync_DerivesAndCrossChecksDatabaseSchemaIdentity()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        BackupService service = new();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAsync(
            fixture.Database, fixture.Documents, fixture.Package, DatabaseInitializer.SchemaVersion - 1, "4.0"));
        Assert.False(File.Exists(fixture.Package));

        await SetPragmaAsync(fixture.Database, "application_id", 123);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAsync(
            fixture.Database, fixture.Documents, fixture.Package, DatabaseInitializer.SchemaVersion, "4.0"));
        Assert.False(File.Exists(fixture.Package));
    }

    [Fact]
    public async Task RestoreAsync_AcceptsAuthenticSchemaOneBackupAndMigratesItToCurrentSchema()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string legacyDatabase = Path.Combine(fixture.Root, "legacy-v1.db");
        DbContextOptions<MhcDbContext> legacyOptions = new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite($"Data Source={legacyDatabase};Pooling=False")
            .Options;
        await using (MhcDbContext legacyContext = new(legacyOptions))
        {
            await legacyContext.Database.MigrateAsync("20260722235510_InitialCreate");
        }
        await SetPragmaAsync(legacyDatabase, "application_id", DatabaseInitializer.ApplicationId);
        await SetPragmaAsync(legacyDatabase, "user_version", 1);

        BackupService service = new();
        string legacyPackage = Path.Combine(fixture.Root, "legacy-v1.mhcbak");
        BackupManifest manifest = await service.CreateAsync(
            legacyDatabase, fixture.Documents, legacyPackage, 1, "3.9");
        string target = Path.Combine(fixture.Root, "restored-v2.db");
        string targetDocuments = Path.Combine(fixture.Root, "restored-v2-documents");

        IRestoreExecution restore = await service.RestoreAsync(
            legacyPackage, target, targetDocuments, 2, true, TestContext.Current.CancellationToken);
        await using (MhcDbContext targetContext = new(new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite($"Data Source={target};Pooling=False")
            .Options))
        {
            await new DatabaseInitializer(targetContext).InitializeAsync(TestContext.Current.CancellationToken);
        }
        await restore.CommitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, manifest.SchemaVersion);
        await using SqliteConnection connection = new($"Data Source={target};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DatabaseInitializer.SchemaVersion, Convert.ToInt32(
            await ScalarAsync(connection, "PRAGMA user_version;"),
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(DatabaseInitializer.SchemaVersion, Convert.ToInt32(
            await ScalarAsync(connection, "SELECT COUNT(*) FROM __EFMigrationsHistory;"),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task RestoreAsync_RequiresExplicitDestructiveConfirmationBeforeExtraction()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        BackupService service = new();
        await service.CreateAsync(fixture.Database, fixture.Documents, fixture.Package, DatabaseInitializer.SchemaVersion, "4.0");
        string target = Path.Combine(fixture.Root, "target.db");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(
            fixture.Package, target, Path.Combine(fixture.Root, "target-documents"), DatabaseInitializer.SchemaVersion,
            destructiveRestoreConfirmed: false));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task RestoreAsync_RejectsEqualAndOverlappingPathsBeforeWriting()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        BackupService service = new();
        await service.CreateAsync(fixture.Database, fixture.Documents, fixture.Package, DatabaseInitializer.SchemaVersion, "4.0");

        await Assert.ThrowsAsync<ArgumentException>(() => service.RestoreAsync(
            fixture.Package, fixture.Package, Path.Combine(fixture.Root, "restored-documents"), DatabaseInitializer.SchemaVersion, true));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RestoreAsync(
            fixture.Package, Path.Combine(fixture.Documents, "restored.db"), fixture.Documents, DatabaseInitializer.SchemaVersion, true));
        Assert.True(File.Exists(fixture.Package));
    }

    [Theory]
    [InlineData("application_id")]
    [InlineData("user_version")]
    [InlineData("migration")]
    [InlineData("table")]
    [InlineData("trigger")]
    public async Task RestoreAsync_RejectsWrongEmbeddedDatabaseIdentityBeforeReplacingTarget(string corruption)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        BackupService service = new();
        await service.CreateAsync(fixture.Database, fixture.Documents, fixture.Package, DatabaseInitializer.SchemaVersion, "4.0");
        string target = Path.Combine(fixture.Root, "target.db");
        await CreateDatabaseAsync(target);
        await SetSettingAsync(target, "target", "preserved");

        await RewriteDatabaseEntryAsync(fixture.Package, async database =>
        {
            switch (corruption)
            {
                case "application_id": await SetPragmaAsync(database, "application_id", 99); break;
                case "user_version":
                    await SetPragmaAsync(database, "user_version", DatabaseInitializer.SchemaVersion + 1);
                    break;
                case "migration":
                    await ExecuteAsync(
                        database,
                        "UPDATE __EFMigrationsHistory SET MigrationId='Wrong' " +
                        "WHERE MigrationId='20260722235510_InitialCreate';");
                    break;
                case "table": await ExecuteAsync(database, "DROP TABLE app_settings;"); break;
                case "trigger": await ExecuteAsync(database, "DROP TRIGGER trg_invoices_no_update;"); break;
            }
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(
            fixture.Package, target, Path.Combine(fixture.Root, "restored-documents"), DatabaseInitializer.SchemaVersion, true));
        Assert.Equal("preserved", await ReadSettingAsync(target, "target"));
    }

    [Theory]
    [InlineData("table")]
    [InlineData("trigger")]
    public async Task RestoreAsync_RejectsDatabaseWithSpoofedCoreSchemaBeforeReplacingTarget(string spoof)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        BackupService service = new();
        await service.CreateAsync(fixture.Database, fixture.Documents, fixture.Package, DatabaseInitializer.SchemaVersion, "4.0");
        string target = Path.Combine(fixture.Root, "target.db");
        await CreateDatabaseAsync(target);
        await SetSettingAsync(target, "target", "preserved");

        await RewriteDatabaseEntryAsync(fixture.Package, async database =>
        {
            if (spoof == "table")
            {
                await ExecuteAsync(database,
                    "ALTER TABLE app_settings RENAME TO real_app_settings; " +
                    "CREATE TABLE app_settings (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL, updated_at_utc_ms INTEGER NOT NULL);");
            }
            else
            {
                await ExecuteAsync(database,
                    "DROP TRIGGER trg_invoices_no_update; " +
                    "CREATE TRIGGER trg_invoices_no_update BEFORE UPDATE ON invoices BEGIN SELECT 1; END;");
            }
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(
            fixture.Package, target, Path.Combine(fixture.Root, "restored-documents"), DatabaseInitializer.SchemaVersion, true));
        Assert.Equal("preserved", await ReadSettingAsync(target, "target"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoverInterruptedRestoreAsync_InvalidOrOversizedJournalFailsClosedWithoutMutatingLivePair(
        bool oversized)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string target = Path.Combine(fixture.Root, "target.db");
        string targetDocuments = Path.Combine(fixture.Root, "documents");
        await File.WriteAllTextAsync(target, "live-database", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(targetDocuments);
        string liveDocument = Path.Combine(targetDocuments, "live.pdf");
        await File.WriteAllTextAsync(liveDocument, "live-document", TestContext.Current.CancellationToken);
        string journal = oversized
            ? $"{{\"id\":\"{new string('a', 5_000)}\",\"databaseExisted\":true,\"documentsExisted\":true}}"
            : "{\"id\":\"0123456789abcdef0123456789abcdef\"}";
        string journalPath = BackupService.GetRestoreJournalPath(target);
        await File.WriteAllTextAsync(journalPath, journal, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BackupService.RecoverInterruptedRestoreAsync(
                target,
                targetDocuments,
                TestContext.Current.CancellationToken));

        Assert.Equal("live-database", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal("live-document", await File.ReadAllTextAsync(liveDocument, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(journalPath));
    }

    [Fact]
    public async Task ReadRestoreJournalAsync_BlocksGrowthAndReplacementWhileReadingBoundedHandle()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string journalPath = Path.Combine(fixture.Root, "restore-journal.json");
        string replacementPath = Path.Combine(fixture.Root, "replacement.json");
        const string id = "0123456789abcdef0123456789abcdef";
        string journal = JsonSerializer.Serialize(new
        {
            Id = id,
            DatabaseExisted = true,
            DocumentsExisted = true,
            Phase = 0,
        });
        await File.WriteAllTextAsync(journalPath, journal, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(replacementPath, journal, TestContext.Current.CancellationToken);

        BackupService.RestoreJournal parsed = await BackupService.ReadRestoreJournalAsync(
            journalPath,
            journalOpened: () =>
            {
                Assert.Throws<IOException>(() => File.AppendAllText(journalPath, " "));
                Exception replacementFailure = Assert.ThrowsAny<Exception>(
                    () => File.Move(replacementPath, journalPath, overwrite: true));
                Assert.True(
                    replacementFailure is IOException or UnauthorizedAccessException,
                    $"Unexpected replacement failure: {replacementFailure.GetType().FullName}");
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(id, parsed.Id);
        Assert.Equal(journal, await File.ReadAllTextAsync(journalPath, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(replacementPath));
    }

    [Fact]
    public async Task RecoverInterruptedRestoreAsync_RestoresOriginalDatabaseAndDocumentsFromDurableJournal()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string target = Path.Combine(fixture.Root, "target.db");
        string targetDocuments = Path.Combine(fixture.Root, "target-documents");
        await CreateDatabaseAsync(target);
        await SetSettingAsync(target, "target", "replacement");
        Directory.CreateDirectory(targetDocuments);
        await File.WriteAllTextAsync(Path.Combine(targetDocuments, "new.pdf"), "new");

        string id = "0123456789abcdef0123456789abcdef";
        string safetyDatabase = Path.Combine(fixture.Root, $"target.db.safety-{id}");
        string safetyDocuments = Path.Combine(fixture.Root, $"target-documents.safety-{id}");
        await CreateDatabaseAsync(safetyDatabase);
        await SetSettingAsync(safetyDatabase, "target", "original");
        Directory.CreateDirectory(safetyDocuments);
        await File.WriteAllTextAsync(Path.Combine(safetyDocuments, "old.pdf"), "old");
        await File.WriteAllTextAsync(
            BackupService.GetRestoreJournalPath(target),
            JsonSerializer.Serialize(new
            {
                Id = id,
                DatabaseExisted = true,
                DocumentsExisted = true,
            }));

        await BackupService.RecoverInterruptedRestoreAsync(target, targetDocuments);

        Assert.Equal("original", await ReadSettingAsync(target, "target"));
        Assert.True(File.Exists(Path.Combine(targetDocuments, "old.pdf")));
        Assert.False(File.Exists(Path.Combine(targetDocuments, "new.pdf")));
        Assert.False(File.Exists(BackupService.GetRestoreJournalPath(target)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root, "*.safety-*"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("database")]
    [InlineData("documents")]
    public async Task RecoverInterruptedRestoreAsync_CommittedCleanupAlwaysPreservesCompleteNewPair(
        string deletedSafetyArtifact)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string target = Path.Combine(fixture.Root, "target.db");
        string targetDocuments = Path.Combine(fixture.Root, "target-documents");
        await CreateDatabaseAsync(target);
        await SetSettingAsync(target, "target", "replacement");
        Directory.CreateDirectory(targetDocuments);
        await File.WriteAllTextAsync(Path.Combine(targetDocuments, "new.pdf"), "new");

        string id = "abcdef0123456789abcdef0123456789";
        string safetyDatabase = Path.Combine(fixture.Root, $"target.db.safety-{id}");
        string safetyDocuments = Path.Combine(fixture.Root, $"target-documents.safety-{id}");
        await CreateDatabaseAsync(safetyDatabase);
        await SetSettingAsync(safetyDatabase, "target", "original");
        Directory.CreateDirectory(safetyDocuments);
        await File.WriteAllTextAsync(Path.Combine(safetyDocuments, "old.pdf"), "old");
        if (deletedSafetyArtifact == "database") File.Delete(safetyDatabase);
        if (deletedSafetyArtifact == "documents") Directory.Delete(safetyDocuments, recursive: true);
        await File.WriteAllTextAsync(
            BackupService.GetRestoreJournalPath(target),
            JsonSerializer.Serialize(new
            {
                Id = id,
                DatabaseExisted = true,
                DocumentsExisted = true,
                Phase = 1,
            }));

        await BackupService.RecoverInterruptedRestoreAsync(target, targetDocuments);

        Assert.Equal("replacement", await ReadSettingAsync(target, "target"));
        Assert.True(File.Exists(Path.Combine(targetDocuments, "new.pdf")));
        Assert.False(File.Exists(Path.Combine(targetDocuments, "old.pdf")));
        Assert.False(File.Exists(BackupService.GetRestoreJournalPath(target)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root, "*.safety-*"));
    }

    [Fact]
    public async Task CommitAsync_WhenDurableMarkerWriteFails_RemainsRollbackCapable()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        BackupService service = new();
        await SetSettingAsync(fixture.Database, "source", "replacement");
        await service.CreateAsync(
            fixture.Database, fixture.Documents, fixture.Package,
            DatabaseInitializer.SchemaVersion, "4.0");
        string target = Path.Combine(fixture.Root, "target.db");
        string targetDocuments = Path.Combine(fixture.Root, "target-documents");
        await CreateDatabaseAsync(target);
        await SetSettingAsync(target, "target", "original");
        Directory.CreateDirectory(targetDocuments);
        await File.WriteAllTextAsync(Path.Combine(targetDocuments, "old.pdf"), "old");

        IRestoreExecution restore = await service.RestoreAsync(
            fixture.Package, target, targetDocuments,
            DatabaseInitializer.SchemaVersion, true, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(BackupService.GetRestoreJournalPath(target) + ".tmp");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            restore.CommitAsync(TestContext.Current.CancellationToken));
        await restore.RollbackAsync(TestContext.Current.CancellationToken);

        Assert.Equal("original", await ReadSettingAsync(target, "target"));
        Assert.True(File.Exists(Path.Combine(targetDocuments, "old.pdf")));
        Assert.False(File.Exists(BackupService.GetRestoreJournalPath(target)));
    }

    [Fact]
    public async Task CommitAsync_WhenSafetyCleanupIsBlocked_RetainsJournalForStartupRetry()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        BackupService service = new();
        await SetSettingAsync(fixture.Database, "source", "replacement");
        await service.CreateAsync(
            fixture.Database, fixture.Documents, fixture.Package,
            DatabaseInitializer.SchemaVersion, "4.0");
        string target = Path.Combine(fixture.Root, "target.db");
        string targetDocuments = Path.Combine(fixture.Root, "target-documents");
        await CreateDatabaseAsync(target);
        await SetSettingAsync(target, "target", "original");
        Directory.CreateDirectory(targetDocuments);
        await File.WriteAllTextAsync(Path.Combine(targetDocuments, "old.pdf"), "old");

        IRestoreExecution restore = await service.RestoreAsync(
            fixture.Package, target, targetDocuments,
            DatabaseInitializer.SchemaVersion, true, TestContext.Current.CancellationToken);
        string safetyDatabase = Assert.IsType<string>(restore.Recovery.SafetyDatabasePath);
        await using (FileStream lockedSafety = new(
            safetyDatabase, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await restore.CommitAsync(TestContext.Current.CancellationToken);
            Assert.True(File.Exists(safetyDatabase));
            Assert.True(File.Exists(BackupService.GetRestoreJournalPath(target)));
        }

        await BackupService.RecoverInterruptedRestoreAsync(target, targetDocuments);

        Assert.Equal("replacement", await ReadSettingAsync(target, "source"));
        Assert.False(File.Exists(safetyDatabase));
        Assert.False(File.Exists(BackupService.GetRestoreJournalPath(target)));
    }

    [Fact]
    public async Task RestoreAsync_PreservesSafetyDatabaseWhenDocumentReplacementFailsAfterDatabaseReplacement()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        BackupService service = new();
        await SetSettingAsync(fixture.Database, "source", "restored");
        await service.CreateAsync(fixture.Database, fixture.Documents, fixture.Package, DatabaseInitializer.SchemaVersion, "4.0");
        string target = Path.Combine(fixture.Root, "target.db");
        await CreateDatabaseAsync(target);
        await SetSettingAsync(target, "target", "preserved");
        string blockedDocumentsPath = Path.Combine(fixture.Root, "blocked-documents");
        await File.WriteAllTextAsync(blockedDocumentsPath, "not a directory");

        RestoreExecutionException exception = await Assert.ThrowsAsync<RestoreExecutionException>(() => service.RestoreAsync(
            fixture.Package, target, blockedDocumentsPath, DatabaseInitializer.SchemaVersion, true));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Equal("preserved", await ReadSettingAsync(target, "target"));
        Assert.Equal(RestorePhase.Replaced, exception.Recovery.Phase);
    }

    [Fact]
    public async Task CopyBoundedAsync_StopsBeforeWritingPastTheActualByteLimit()
    {
        await using MemoryStream input = new(new byte[128]);
        await using MemoryStream output = new();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BackupService.CopyBoundedAsync(input, output, 64, TestContext.Current.CancellationToken));

        Assert.InRange(output.Length, 0, 64);
    }

    [Fact]
    public async Task RestoreAsync_RejectsZipResourceLimitViolationsBeforeReplacingTarget()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        BackupService creator = new();
        await creator.CreateAsync(fixture.Database, fixture.Documents, fixture.Package, DatabaseInitializer.SchemaVersion, "4.0");
        BackupService constrained = new(resourceLimits: new BackupResourceLimits(
            MaxEntryCount: 1, MaxEntryUncompressedBytes: 1024 * 1024,
            MaxTotalUncompressedBytes: 1024 * 1024, MaxCompressionRatio: 100));
        string target = Path.Combine(fixture.Root, "target.db");

        await Assert.ThrowsAsync<InvalidDataException>(() => constrained.RestoreAsync(
            fixture.Package, target, Path.Combine(fixture.Root, "target-documents"), DatabaseInitializer.SchemaVersion, true));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task CreateAsync_RejectsResourceLimitsBeforePublishingPackage()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await File.WriteAllBytesAsync(
            Path.Combine(fixture.Documents, "oversized.pdf"),
            new byte[17],
            TestContext.Current.CancellationToken);
        BackupService constrained = new(resourceLimits: new BackupResourceLimits(
            MaxEntryCount: 10,
            MaxEntryUncompressedBytes: 16,
            MaxTotalUncompressedBytes: 1024 * 1024,
            MaxCompressionRatio: 1000));

        await Assert.ThrowsAsync<InvalidDataException>(() => constrained.CreateAsync(
            fixture.Database,
            fixture.Documents,
            fixture.Package,
            DatabaseInitializer.SchemaVersion,
            "4.0",
            TestContext.Current.CancellationToken));

        Assert.False(File.Exists(fixture.Package));
    }

    [Fact]
    public async Task CreateAsync_RejectsArchiveItsOwnRestoreLimitsWouldReject()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        BackupService constrained = new(resourceLimits: new BackupResourceLimits(
            MaxEntryCount: 100,
            MaxEntryUncompressedBytes: 1024 * 1024,
            MaxTotalUncompressedBytes: 10 * 1024 * 1024,
            MaxCompressionRatio: 1));

        await Assert.ThrowsAsync<InvalidDataException>(() => constrained.CreateAsync(
            fixture.Database,
            fixture.Documents,
            fixture.Package,
            DatabaseInitializer.SchemaVersion,
            "4.0",
            TestContext.Current.CancellationToken));

        Assert.False(File.Exists(fixture.Package));
    }

    [Fact]
    public async Task RestoreAsync_HonorsCancellationDuringExtraction()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        BackupService service = new();
        await service.CreateAsync(fixture.Database, fixture.Documents, fixture.Package, DatabaseInitializer.SchemaVersion, "4.0");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RestoreAsync(
            fixture.Package, Path.Combine(fixture.Root, "target.db"),
            Path.Combine(fixture.Root, "target-documents"), DatabaseInitializer.SchemaVersion, true, cancellation.Token));
    }

    [Fact]
    public async Task CreateAndRestoreAsync_RestoresVerifiedDatabaseAndDocuments()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await File.WriteAllBytesAsync(Path.Combine(fixture.Documents, "invoice.pdf"), [1, 2, 3]);
        await using SqliteConnection writer = new($"Data Source={fixture.Database};Pooling=False");
        await writer.OpenAsync();
        await ExecuteAsync(writer, "PRAGMA journal_mode=WAL;");
        await using (SqliteCommand marker = writer.CreateCommand())
        {
            marker.CommandText = "INSERT INTO app_settings (key, value, updated_at_utc_ms) VALUES ('marker', 'captured', 1);";
            await marker.ExecuteNonQueryAsync();
        }
        BackupService service = new();
        BackupManifest manifest = await service.CreateAsync(
            fixture.Database, fixture.Documents, fixture.Package, DatabaseInitializer.SchemaVersion, "4.0");
        string target = Path.Combine(fixture.Root, "target.db");
        string targetDocuments = Path.Combine(fixture.Root, "target-documents");
        await CreateDatabaseAsync(target);
        Directory.CreateDirectory(targetDocuments);
        await File.WriteAllTextAsync(Path.Combine(targetDocuments, "old.pdf"), "old");

        IRestoreExecution restore = await service.RestoreAsync(fixture.Package, target, targetDocuments, DatabaseInitializer.SchemaVersion, true);

        Assert.NotEmpty(restore.Recovery.RetainedPaths);
        await restore.CommitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DatabaseInitializer.SchemaVersion, manifest.SchemaVersion);
        Assert.Equal("captured", await ReadSettingAsync(target, "marker"));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(targetDocuments, "invoice.pdf")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root, "*.safety-*"));
    }

    [Fact]
    public async Task RestoreExecution_RollbackAtomicallyRestoresDatabaseAndDocumentsAndCleansCandidate()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await SetSettingAsync(fixture.Database, "source", "replacement");
        await File.WriteAllTextAsync(Path.Combine(fixture.Documents, "new.pdf"), "new");
        BackupService service = new();
        await service.CreateAsync(fixture.Database, fixture.Documents, fixture.Package, DatabaseInitializer.SchemaVersion, "4.0");
        string target = Path.Combine(fixture.Root, "target.db");
        string targetDocuments = Path.Combine(fixture.Root, "target-documents");
        await CreateDatabaseAsync(target);
        await SetSettingAsync(target, "target", "original");
        Directory.CreateDirectory(targetDocuments);
        await File.WriteAllTextAsync(Path.Combine(targetDocuments, "old.pdf"), "old");

        IRestoreExecution restore = await service.RestoreAsync(
            fixture.Package, target, targetDocuments, DatabaseInitializer.SchemaVersion, true, TestContext.Current.CancellationToken);
        await restore.RollbackAsync(TestContext.Current.CancellationToken);

        Assert.Equal("original", await ReadSettingAsync(target, "target"));
        Assert.True(File.Exists(Path.Combine(targetDocuments, "old.pdf")));
        Assert.False(File.Exists(Path.Combine(targetDocuments, "new.pdf")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root, "*restore-*"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root, "*.safety-*"));
    }

    private static async Task RewriteDatabaseEntryAsync(string package, Func<string, Task> mutate)
    {
        string root = Path.Combine(Path.GetTempPath(), $"backup-rewrite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            ZipFile.ExtractToDirectory(package, root);
            string database = Path.Combine(root, "database.sqlite");
            await mutate(database);
            string manifestPath = Path.Combine(root, "manifest.json");
            BackupManifest manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            byte[] bytes = await File.ReadAllBytesAsync(database);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            BackupManifest updated = manifest with
            {
                Files = manifest.Files.Select(file => file.Path == "database.sqlite"
                    ? file with { Length = bytes.LongLength, Sha256 = hash }
                    : file).ToArray(),
            };
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(updated,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            File.Delete(package);
            ZipFile.CreateFromDirectory(root, package);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task SeedFinalizedInvoiceAsync(string path)
    {
        DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;
        await using MhcDbContext context = new(options);
        long issuedAt = 1_774_460_400_000;
        byte[] pdf = "%PDF-1.7 canonical"u8.ToArray();
        byte[] hash = SHA256.HashData(pdf);
        InvoiceEntity invoice = new()
        {
            Id = Guid.CreateVersion7(),
            IssuanceYear = 2026,
            Sequence = 100,
            PublicNumber = "MHC-2026-100",
            DocumentType = InvoiceDocumentType.TaxInvoice,
            BusinessDate = "2026-03-25",
            IssuedAtUtcMs = issuedAt,
            IssuedAtSaudiLocal = "2026-03-25T20:40:00.000+03:00",
            IssuedSaudiOffsetMinutes = 180,
            SellerNameArabic = "MHC Technology",
            SellerVatNumber = "310123456789003",
            SellerBranch = "Riyadh",
            SellerAddress = "Riyadh",
            OperatorName = "Operator",
            CustomerNameArabic = "Customer",
            CustomerSearchName = "customer",
            PaymentMethod = PaymentMethod.Cash,
            Currency = "SAR",
            SubtotalHalalah = 100,
            VatHalalah = 15,
            GrandTotalHalalah = 115,
            Document = new InvoiceDocumentEntity
            {
                PdfBytes = pdf,
                Sha256 = hash,
                ByteLength = pdf.LongLength,
                CreatedAtUtcMs = issuedAt,
            },
        };
        invoice.Lines.Add(new InvoiceLineEntity
        {
            Id = Guid.CreateVersion7(),
            Position = 0,
            Description = "Service",
            Unit = "unit",
            QuantityMilliunits = 1_000,
            UnitPriceHalalah = 100,
            VatCategory = VatCategory.Standard15,
            NetHalalah = 100,
            VatHalalah = 15,
            GrossHalalah = 115,
        });
        context.Invoices.Add(invoice);
        context.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            InvoiceId = invoice.Id,
            EventType = 1,
            OccurredAtUtcMs = issuedAt,
            OperatorName = invoice.OperatorName,
        });
        await context.SaveChangesAsync();
        await CanonicalInvoiceFinalizer.FinalizeAsync(
            context, invoice.Id, issuedAt, hash, TestContext.Current.CancellationToken);
    }

    private static async Task CreateDatabaseAsync(string path)
    {
        DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;
        await using MhcDbContext context = new(options);
        await new DatabaseInitializer(context).InitializeAsync();
    }

    private static async Task SetSettingAsync(string path, string key, string value)
    {
        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO app_settings (key, value, updated_at_utc_ms) VALUES ($key, $value, 1);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadSettingAsync(string path, string key)
    {
        await using SqliteConnection connection = new($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task SetPragmaAsync(string path, string pragma, int value) =>
        await ExecuteAsync(path, $"PRAGMA {pragma}={value};");

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await ExecuteAsync(connection, sql);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root)
        {
            Root = root;
            Database = Path.Combine(root, "source.db");
            Documents = Path.Combine(root, "documents");
            Package = Path.Combine(root, "backup.mhcbackup");
        }

        public string Root { get; }
        public string Database { get; }
        public string Documents { get; }
        public string Package { get; }

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new(Path.Combine(Path.GetTempPath(), $"backup-tests-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(fixture.Root);
            Directory.CreateDirectory(fixture.Documents);
            await CreateDatabaseAsync(fixture.Database);
            return fixture;
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}

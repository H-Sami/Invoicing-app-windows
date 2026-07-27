using MHC.Invoicing.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Tests.Persistence;

public sealed class DatabaseInitializerTests
{
    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, true)]
    public void PreMigrationBackupDecision_ProtectsOnlyExistingOlderSchemas(
        int currentVersion,
        int supportedVersion,
        bool expected)
    {
        Assert.Equal(
            expected,
            DatabaseInitializer.RequiresPreMigrationBackup(currentVersion, supportedVersion));
    }

    [Fact]
    public async Task PreMigrationBackup_CapturesCommittedWalStateInVerifiedCopy()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}");
        string databasePath = Path.Combine(directory, "mhc-invoices.db");
        Directory.CreateDirectory(directory);
        try
        {
            await using SqliteConnection writer = new($"Data Source={databasePath}");
            await writer.OpenAsync(cancellationToken);
            await ExecuteAsync(writer, "PRAGMA journal_mode = WAL;", cancellationToken);
            await ExecuteAsync(writer, "CREATE TABLE marker (value TEXT NOT NULL);", cancellationToken);
            await ExecuteAsync(writer, "INSERT INTO marker (value) VALUES ('committed-wal');", cancellationToken);

            PreMigrationBackupService service = new();
            string backupPath = await service.CreateVerifiedCopyAsync(
                databasePath,
                sourceSchemaVersion: 1,
                cancellationToken);

            Assert.True(File.Exists(backupPath));
            await using SqliteConnection backup = new($"Data Source={backupPath};Mode=ReadOnly;Pooling=False");
            await backup.OpenAsync(cancellationToken);
            Assert.Equal("committed-wal", await ExecuteScalarAsync(backup, "SELECT value FROM marker;", cancellationToken));
            Assert.Equal("ok", await ExecuteScalarAsync(backup, "PRAGMA integrity_check;", cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_AppliesMigrationsAndHardensConnection()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}");
        string databasePath = Path.Combine(directory, "mhc-invoices.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            DatabaseInitializer initializer = new(context);

            await initializer.InitializeAsync(cancellationToken);

            Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
            Assert.Equal("wal", await ReadTextPragmaAsync(context, "journal_mode", cancellationToken));
            Assert.Equal(1L, await ReadIntegerPragmaAsync(context, "foreign_keys", cancellationToken));
            Assert.Equal(DatabaseInitializer.ApplicationId, await ReadIntegerPragmaAsync(context, "application_id", cancellationToken));
            Assert.Equal(DatabaseInitializer.SchemaVersion, await ReadIntegerPragmaAsync(context, "user_version", cancellationToken));
            Assert.True(await TableExistsAsync(context, "invoices", cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_RejectsSpoofedApplicationIdAtSchemaVersionZeroWithoutMutatingDatabase()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}");
        string databasePath = Path.Combine(directory, "foreign.db");
        Directory.CreateDirectory(directory);
        try
        {
            await using (SqliteConnection writer = new($"Data Source={databasePath};Pooling=False"))
            {
                await writer.OpenAsync(cancellationToken);
                await ExecuteAsync(writer, $"PRAGMA application_id = {DatabaseInitializer.ApplicationId};", cancellationToken);
                await ExecuteAsync(writer, "CREATE TABLE foreign_marker (value TEXT NOT NULL);", cancellationToken);
                await ExecuteAsync(writer, "INSERT INTO foreign_marker (value) VALUES ('preserve-me');", cancellationToken);
            }

            await using MhcDbContext context = CreateContext(databasePath);
            await Assert.ThrowsAsync<DatabaseInitializationException>(() =>
                new DatabaseInitializer(context).InitializeAsync(cancellationToken));

            await using SqliteConnection verifier = new($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
            await verifier.OpenAsync(cancellationToken);
            Assert.Equal(
                "preserve-me",
                await ExecuteScalarAsync(verifier, "SELECT value FROM foreign_marker;", cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_BacksUpAuthenticSchemaOneDatabaseBeforeMigratingToCurrentSchema()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}");
        string databasePath = Path.Combine(directory, "mhc-invoices.db");
        Directory.CreateDirectory(directory);
        try
        {
            await using (MhcDbContext legacyContext = CreateContext(databasePath))
            {
                await legacyContext.Database.MigrateAsync(
                    "20260722235510_InitialCreate",
                    cancellationToken);
            }

            await using (SqliteConnection legacyConnection = new($"Data Source={databasePath}"))
            {
                await legacyConnection.OpenAsync(cancellationToken);
                await ExecuteAsync(
                    legacyConnection,
                    $"PRAGMA application_id = {DatabaseInitializer.ApplicationId}; PRAGMA user_version = 1;",
                    cancellationToken);
            }

            await using MhcDbContext currentContext = CreateContext(databasePath);
            await new DatabaseInitializer(currentContext).InitializeAsync(cancellationToken);

            Assert.Equal(
                DatabaseInitializer.SchemaVersion,
                await ReadIntegerPragmaAsync(currentContext, "user_version", cancellationToken));
            string backupPath = Assert.Single(Directory.GetFiles(
                Path.Combine(directory, "PreMigrationBackups"),
                "mhc-invoices-pre-migration-v1-*.db",
                SearchOption.TopDirectoryOnly));
            await using SqliteConnection backup = new($"Data Source={backupPath};Mode=ReadOnly;Pooling=False");
            await backup.OpenAsync(cancellationToken);
            Assert.Equal(1L, Convert.ToInt64(
                await ExecuteScalarAsync(backup, "PRAGMA user_version;", cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(1L, Convert.ToInt64(
                await ExecuteScalarAsync(
                    backup,
                    "SELECT COUNT(*) FROM __EFMigrationsHistory;",
                    cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("ok", Convert.ToString(
                await ExecuteScalarAsync(backup, "PRAGMA integrity_check;", cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_RejectsCurrentSchemaWithDroppedAccountingTrigger()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}");
        string databasePath = Path.Combine(directory, "mhc-invoices.db");
        try
        {
            await using (MhcDbContext initialContext = CreateContext(databasePath))
            {
                await new DatabaseInitializer(initialContext).InitializeAsync(cancellationToken);
            }
            await using (SqliteConnection tamper = new($"Data Source={databasePath};Pooling=False"))
            {
                await tamper.OpenAsync(cancellationToken);
                await ExecuteAsync(tamper, "DROP TRIGGER trg_invoice_voids_create_audit;", cancellationToken);
            }

            await using MhcDbContext reopenedContext = CreateContext(databasePath);
            DatabaseInitializationException exception = await Assert.ThrowsAsync<DatabaseInitializationException>(
                () => new DatabaseInitializer(reopenedContext).InitializeAsync(cancellationToken));

            Assert.IsType<InvalidDataException>(exception.InnerException);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_RejectsNonEmptyUnmarkedDatabaseBeforeMigration()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}");
        string databasePath = Path.Combine(directory, "mhc-invoices.db");
        Directory.CreateDirectory(directory);
        try
        {
            await using (SqliteConnection connection = new($"Data Source={databasePath}"))
            {
                await connection.OpenAsync(cancellationToken);
                await ExecuteAsync(connection, "CREATE TABLE unrelated_data (value TEXT NOT NULL);", cancellationToken);
                await ExecuteAsync(connection, "INSERT INTO unrelated_data (value) VALUES ('preserve me');", cancellationToken);
            }

            await using MhcDbContext context = CreateContext(databasePath);
            DatabaseInitializer initializer = new(context);

            DatabaseInitializationException exception = await Assert.ThrowsAsync<DatabaseInitializationException>(
                () => initializer.InitializeAsync(cancellationToken));

            Assert.Contains("unrecognized non-empty", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(await TableExistsAsync(context, "invoices", cancellationToken));
            Assert.True(await TableExistsAsync(context, "unrelated_data", cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_RejectsForeignOrNewerDatabaseFiles()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}");
        string databasePath = Path.Combine(directory, "mhc-invoices.db");
        Directory.CreateDirectory(directory);
        try
        {
            await using (SqliteConnection connection = new($"Data Source={databasePath}"))
            {
                await connection.OpenAsync(cancellationToken);
                await ExecuteAsync(connection, "PRAGMA application_id = 42;", cancellationToken);
            }

            await using MhcDbContext context = CreateContext(databasePath);
            DatabaseInitializer initializer = new(context);

            DatabaseInitializationException exception = await Assert.ThrowsAsync<DatabaseInitializationException>(
                () => initializer.InitializeAsync(cancellationToken));

            Assert.Contains("application identifier", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static MhcDbContext CreateContext(string databasePath)
    {
        DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new MhcDbContext(options);
    }

    private static async Task<long> ReadIntegerPragmaAsync(
        DbContext context,
        string pragma,
        CancellationToken cancellationToken)
    {
        object? value = await ExecuteScalarAsync(context, $"PRAGMA {pragma};", cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadTextPragmaAsync(
        DbContext context,
        string pragma,
        CancellationToken cancellationToken)
    {
        object? value = await ExecuteScalarAsync(context, $"PRAGMA {pragma};", cancellationToken);
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async Task<bool> TableExistsAsync(
        DbContext context,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using System.Data.Common.DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        System.Data.Common.DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        await context.Database.OpenConnectionAsync(cancellationToken);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<object?> ExecuteScalarAsync(
        DbContext context,
        string sql,
        CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using System.Data.Common.DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<object?> ExecuteScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

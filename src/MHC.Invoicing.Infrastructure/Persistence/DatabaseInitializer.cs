using System.Data.Common;
using MHC.Invoicing.Infrastructure.Backup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Persistence;

public sealed class DatabaseInitializer(
    MhcDbContext context,
    PreMigrationBackupService? preMigrationBackupService = null)
{
    public const int ApplicationId = 0x4D484334;
    public const int SchemaVersion = 3;
    private static readonly SemaphoreSlim InitializationLock = new(1, 1);
    private readonly PreMigrationBackupService _preMigrationBackupService =
        preMigrationBackupService ?? new PreMigrationBackupService();

    public static bool RequiresPreMigrationBackup(int currentVersion, int supportedVersion) =>
        currentVersion > 0 && currentVersion < supportedVersion;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await InitializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool closeConnection = context.Database.GetDbConnection().State != System.Data.ConnectionState.Open;
        try
        {
            EnsureDatabaseDirectory();
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            long applicationId = await ReadIntegerPragmaAsync("application_id", cancellationToken).ConfigureAwait(false);
            if (applicationId is not 0 and not ApplicationId)
            {
                throw new DatabaseInitializationException(
                    $"The selected file has an unexpected SQLite application identifier ({applicationId}).");
            }

            long userVersion = await ReadIntegerPragmaAsync("user_version", cancellationToken).ConfigureAwait(false);
            if (userVersion == 0 &&
                (applicationId != 0 || await HasUserObjectsAsync(cancellationToken).ConfigureAwait(false)))
            {
                throw new DatabaseInitializationException(
                    "The selected file is an unrecognized non-empty SQLite database.");
            }

            if (userVersion > SchemaVersion)
            {
                throw new DatabaseInitializationException(
                    $"Database schema version {userVersion} is newer than supported version {SchemaVersion}.");
            }

            if (userVersion > 0 && TryGetDatabasePath(out string existingDatabasePath))
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
                try
                {
                    await BackupService.ValidateSupportedCurrentDatabaseAsync(
                        existingDatabasePath, checked((int)userVersion), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (RequiresPreMigrationBackup(checked((int)userVersion), SchemaVersion) &&
                TryGetDatabasePath(out string databasePath))
            {
                await _preMigrationBackupService.CreateVerifiedCopyAsync(
                    databasePath,
                    checked((int)userVersion),
                    cancellationToken).ConfigureAwait(false);
            }

            string journalMode = await ReadTextPragmaAsync("journal_mode = WAL", cancellationToken).ConfigureAwait(false);
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new DatabaseInitializationException("SQLite refused to enable write-ahead logging.");
            }

            await ExecuteAsync("PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync("PRAGMA synchronous = FULL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync("PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);

            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteAsync($"PRAGMA application_id = {ApplicationId};", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync($"PRAGMA user_version = {SchemaVersion};", cancellationToken).ConfigureAwait(false);
            await VerifyIntegrityAsync(cancellationToken).ConfigureAwait(false);
            if (TryGetDatabasePath(out string admittedDatabasePath))
            {
                bool reopenConnection = !closeConnection;
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
                try
                {
                    await BackupService.ValidateSupportedCurrentDatabaseAsync(
                        admittedDatabasePath, SchemaVersion, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (reopenConnection)
                        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (DatabaseInitializationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SqliteException or DbException or InvalidOperationException or InvalidDataException)
        {
            throw new DatabaseInitializationException("The local invoice database could not be initialized safely.", exception);
        }
        finally
        {
            try
            {
                if (closeConnection)
                {
                    await context.Database.CloseConnectionAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                InitializationLock.Release();
            }
        }
    }

    private void EnsureDatabaseDirectory()
    {
        string connectionString = context.Database.GetConnectionString() ?? string.Empty;
        SqliteConnectionStringBuilder builder = new(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:")
        {
            return;
        }

        string fullPath = Path.GetFullPath(builder.DataSource);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
    }

    private bool TryGetDatabasePath(out string databasePath)
    {
        string connectionString = context.Database.GetConnectionString() ?? string.Empty;
        SqliteConnectionStringBuilder builder = new(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:")
        {
            databasePath = string.Empty;
            return false;
        }

        databasePath = Path.GetFullPath(builder.DataSource);
        return true;
    }

    private async Task VerifyIntegrityAsync(CancellationToken cancellationToken)
    {
        string quickCheck = await ReadTextPragmaAsync("quick_check", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(quickCheck, "ok", StringComparison.Ordinal))
        {
            throw new DatabaseInitializationException($"SQLite quick check failed: {quickCheck}");
        }

        await using DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DatabaseInitializationException("SQLite foreign-key integrity check failed.");
        }
    }

    private async Task<bool> HasUserObjectsAsync(CancellationToken cancellationToken)
    {
        object? value = await ExecuteScalarAsync(
            "SELECT EXISTS (SELECT 1 FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%');",
            cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private async Task<long> ReadIntegerPragmaAsync(string pragma, CancellationToken cancellationToken)
    {
        object? value = await ExecuteScalarAsync($"PRAGMA {pragma};", cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string> ReadTextPragmaAsync(string pragma, CancellationToken cancellationToken)
    {
        object? value = await ExecuteScalarAsync($"PRAGMA {pragma};", cancellationToken).ConfigureAwait(false);
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task<object?> ExecuteScalarAsync(string sql, CancellationToken cancellationToken)
    {
        await using DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class DatabaseInitializationException : InvalidOperationException
{
    public DatabaseInitializationException(string message)
        : base(message)
    {
    }

    public DatabaseInitializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

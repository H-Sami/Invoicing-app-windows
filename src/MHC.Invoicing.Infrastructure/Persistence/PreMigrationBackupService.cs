using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MHC.Invoicing.Infrastructure.Persistence;

public sealed class PreMigrationBackupService(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<string> CreateVerifiedCopyAsync(
        string databasePath,
        int sourceSchemaVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("The database path is required.", nameof(databasePath));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(sourceSchemaVersion);
        string fullSourcePath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("The database to protect does not exist.", fullSourcePath);
        }

        string databaseDirectory = Path.GetDirectoryName(fullSourcePath)
            ?? throw new InvalidOperationException("The database directory could not be resolved.");
        string backupDirectory = Path.Combine(databaseDirectory, "PreMigrationBackups");
        Directory.CreateDirectory(backupDirectory);
        string timestamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        string destinationPath = Path.Combine(
            backupDirectory,
            $"mhc-invoices-pre-migration-v{sourceSchemaVersion}-{timestamp}-{Guid.NewGuid():N}.db");

        try
        {
            await using (SqliteConnection source = new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = fullSourcePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    DefaultTimeout = 5,
                }.ToString()))
            await using (SqliteConnection destination = new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = destinationPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                    DefaultTimeout = 5,
                }.ToString()))
            {
                await source.OpenAsync(cancellationToken);
                await destination.OpenAsync(cancellationToken);
                source.BackupDatabase(destination);
            }

            await using SqliteConnection verification = new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = destinationPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    DefaultTimeout = 5,
                }.ToString());
            await verification.OpenAsync(cancellationToken);
            await using SqliteCommand command = verification.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            string result = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) ?? string.Empty;
            if (!string.Equals(result, "ok", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Pre-migration SQLite integrity check failed: {result}");
            }

            return destinationPath;
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                string path = destinationPath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            throw;
        }
    }
}

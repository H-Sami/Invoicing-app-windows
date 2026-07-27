using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MHC.Invoicing.Application.Maintenance;
using MHC.Invoicing.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Backup;

public sealed record BackupFileEntry(string Path, string Sha256, long Length);

public sealed record BackupManifest(
    int FormatVersion,
    int SchemaVersion,
    string ApplicationVersion,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<BackupFileEntry> Files);

public sealed record BackupResourceLimits(
    int MaxEntryCount = 10_000,
    long MaxEntryUncompressedBytes = 512L * 1024 * 1024,
    long MaxTotalUncompressedBytes = 2L * 1024 * 1024 * 1024,
    double MaxCompressionRatio = 1_000)
{
    internal void Validate()
    {
        if (MaxEntryCount <= 0 || MaxEntryUncompressedBytes <= 0 ||
            MaxTotalUncompressedBytes <= 0 || MaxCompressionRatio < 1 ||
            double.IsNaN(MaxCompressionRatio))
            throw new ArgumentOutOfRangeException(nameof(BackupResourceLimits));
    }
}

public sealed class BackupService
{
    private const int CurrentFormatVersion = 1;
    private const int ExpectedApplicationId = 0x4D484334;
    private sealed record SupportedSchema(string Fingerprint, string[] Migrations);

    private static readonly Dictionary<int, SupportedSchema> SupportedSchemas =
        new Dictionary<int, SupportedSchema>
        {
            [1] = new(
                "eb9f5ecb8f4378fd351aa0f08433ed279fec570ae31450c4d09d8b43cda6d680",
                ["20260722235510_InitialCreate"]),
            [2] = new(
                "480c703346c578cbe1d0a466ef440e1c2048ddb43efaab78d9d10eabb131b31f",
                ["20260722235510_InitialCreate", "20260724003000_RejectVoidedOriginalCreditFinalization"]),
            [3] = new(
                "2b348d722f9937182edab179cc9ae951bd9794a631bfc2d3523895275a6e89cb",
                ["20260722235510_InitialCreate", "20260724003000_RejectVoidedOriginalCreditFinalization",
                 "20260724010000_StrengthenIssuanceLedgerValidation"]),
        };
    private static readonly string[] RequiredTables =
    [
        "__EFMigrationsHistory", "app_settings", "catalog_items", "company_profiles", "customers",
        "invoice_sequences", "invoices", "audit_events", "invoice_documents", "invoice_drafts",
        "invoice_lines", "invoice_voids", "invoice_draft_lines", "invoice_finalizations",
    ];
    private static readonly string[] RequiredTriggers =
    [
        "trg_invoice_finalizations_validate", "trg_invoice_lines_no_insert_after_finalization",
        "trg_invoice_documents_no_insert_after_finalization", "trg_invoice_finalizations_no_update",
        "trg_invoice_finalizations_no_delete", "trg_invoices_no_update", "trg_invoices_no_delete",
        "trg_invoice_lines_no_update", "trg_invoice_lines_no_delete", "trg_invoice_documents_no_update",
        "trg_invoice_documents_no_delete", "trg_invoice_voids_no_update", "trg_invoice_voids_no_delete",
        "trg_audit_events_no_update", "trg_audit_events_no_delete",
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    internal enum RestoreCommitPhase
    {
        Installing = 0,
        CommittingCleanup = 1,
    }

    private const long MaxRestoreJournalBytes = 4 * 1024;
    internal sealed record RestoreJournal(
        [property: JsonRequired] string Id,
        [property: JsonRequired] bool DatabaseExisted,
        [property: JsonRequired] bool DocumentsExisted,
        RestoreCommitPhase Phase = RestoreCommitPhase.Installing);

    private readonly TimeProvider _timeProvider;
    private readonly BackupResourceLimits _resourceLimits;

    public BackupService(TimeProvider? timeProvider = null, BackupResourceLimits? resourceLimits = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resourceLimits = resourceLimits ?? new BackupResourceLimits();
        _resourceLimits.Validate();
    }

    internal static string GetRestoreJournalPath(string databasePath) =>
        Path.GetFullPath(databasePath) + ".restore-journal.json";

    public static async Task RecoverInterruptedRestoreAsync(
        string databasePath,
        string documentsDirectory,
        CancellationToken cancellationToken = default)
    {
        string databaseFullPath = Path.GetFullPath(databasePath);
        string documentsFullPath = TrimEndingSeparator(Path.GetFullPath(documentsDirectory));
        string journalPath = GetRestoreJournalPath(databaseFullPath);
        if (!File.Exists(journalPath))
            return;

        RestoreJournal journal = await ReadRestoreJournalAsync(journalPath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (journal.Id.Length != 32 || journal.Id.Any(static character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The restore recovery journal is invalid.");
        if (!Enum.IsDefined(journal.Phase))
            throw new InvalidDataException("The restore recovery journal has an unsupported phase.");

        string databaseDirectory = Path.GetDirectoryName(databaseFullPath)!;
        string documentsParent = Path.GetDirectoryName(documentsFullPath)!;
        string stagedDatabase = Path.Combine(databaseDirectory, $".{Path.GetFileName(databaseFullPath)}.restore-{journal.Id}");
        string safetyDatabase = Path.Combine(databaseDirectory, $"{Path.GetFileName(databaseFullPath)}.safety-{journal.Id}");
        string stagedDocuments = Path.Combine(documentsParent, $".{Path.GetFileName(documentsFullPath)}.restore-{journal.Id}");
        string safetyDocuments = Path.Combine(documentsParent, $"{Path.GetFileName(documentsFullPath)}.safety-{journal.Id}");

        if (journal.Phase == RestoreCommitPhase.CommittingCleanup)
        {
            // The new (database, documents) pair is authoritative; safety artifacts are stale.
            // Complete commit cleanup idempotently and delete the journal.
            SqliteConnection.ClearAllPools();
            TryDeleteFile(stagedDatabase);
            DeleteSidecars(stagedDatabase);
            TryDeleteDirectory(stagedDocuments);
            TryDeleteFile(safetyDatabase);
            DeleteSidecars(safetyDatabase);
            TryDeleteDirectory(safetyDocuments);
            if (!RestoreCleanupArtifactsExist(
                    stagedDatabase, stagedDocuments, safetyDatabase, safetyDocuments))
            {
                TryDeleteFile(journalPath);
            }
            return;
        }

        // Phase == Installing: the original (database, documents) pair must be restored.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(safetyDocuments))
        {
            TryDeleteDirectory(stagedDocuments);
            if (Directory.Exists(documentsFullPath))
                Directory.Move(documentsFullPath, stagedDocuments);
            Directory.Move(safetyDocuments, documentsFullPath);
        }
        else if (!journal.DocumentsExisted)
        {
            TryDeleteDirectory(documentsFullPath);
        }

        DeleteSidecars(databaseFullPath);
        if (File.Exists(safetyDatabase))
        {
            TryDeleteFile(stagedDatabase);
            if (File.Exists(databaseFullPath))
                File.Replace(safetyDatabase, databaseFullPath, stagedDatabase, ignoreMetadataErrors: true);
            else
                File.Move(safetyDatabase, databaseFullPath);
        }
        else if (!journal.DatabaseExisted)
        {
            TryDeleteFile(databaseFullPath);
        }

        TryDeleteFile(stagedDatabase);
        DeleteSidecars(stagedDatabase);
        TryDeleteDirectory(stagedDocuments);
        TryDeleteFile(safetyDatabase);
        DeleteSidecars(safetyDatabase);
        TryDeleteDirectory(safetyDocuments);
        File.Delete(journalPath);
    }

    internal static async Task<RestoreJournal> ReadRestoreJournalAsync(
        string journalPath,
        Action? journalOpened = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using FileStream stream = new(
                journalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                bufferSize: (int)MaxRestoreJournalBytes + 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            journalOpened?.Invoke();
            if (stream.Length > MaxRestoreJournalBytes)
                throw new InvalidDataException("The restore recovery journal is too large.");
            byte[] journalBytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(journalBytes, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RestoreJournal>(journalBytes, JsonOptions)
                ?? throw new InvalidDataException("The restore recovery journal is invalid.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The restore recovery journal is invalid.", exception);
        }
    }

    private static async Task WriteRestoreJournalAsync(
        string journalPath,
        RestoreJournal journal,
        CancellationToken cancellationToken)
    {
        string temporaryPath = journalPath + ".tmp";
        TryDeleteFile(temporaryPath);
        await using (FileStream stream = new(
            temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, journal, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, journalPath, overwrite: true);
    }

    public async Task<BackupManifest> CreateAsync(
        string databasePath,
        string documentsDirectory,
        string packagePath,
        int schemaVersion,
        string applicationVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateCreateArguments(databasePath, documentsDirectory, packagePath, schemaVersion, applicationVersion);
        string packageFullPath = Path.GetFullPath(packagePath);
        string packageDirectory = Path.GetDirectoryName(packageFullPath)!;
        Directory.CreateDirectory(packageDirectory);
        string workDirectory = Path.Combine(packageDirectory, $".backup-{Guid.NewGuid():N}");
        string temporaryPackage = Path.Combine(packageDirectory, $".{Path.GetFileName(packageFullPath)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(workDirectory);
        try
        {
            string databaseCopy = Path.Combine(workDirectory, "database.sqlite");
            await OnlineBackupAsync(databasePath, databaseCopy, cancellationToken).ConfigureAwait(false);
            DatabaseIdentity identity = await ValidateDatabaseAsync(databaseCopy, schemaVersion, cancellationToken)
                .ConfigureAwait(false);
            DeleteSidecars(databaseCopy);

            List<BackupFileEntry> files =
            [
                await CreateFileEntryAsync(databaseCopy, "database.sqlite", cancellationToken).ConfigureAwait(false),
            ];
            if (Directory.Exists(documentsDirectory))
            {
                foreach (string sourcePath in Directory.EnumerateFiles(documentsDirectory, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relativePath = Path.GetRelativePath(documentsDirectory, sourcePath);
                    string targetPath = Path.Combine(workDirectory, "documents", relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    await CopyFileAsync(sourcePath, targetPath, cancellationToken).ConfigureAwait(false);
                    files.Add(await CreateFileEntryAsync(
                        targetPath, $"documents/{relativePath.Replace('\\', '/')}", cancellationToken).ConfigureAwait(false));
                }
            }

            BackupManifest manifest = new(
                CurrentFormatVersion,
                identity.UserVersion,
                applicationVersion.Trim(),
                _timeProvider.GetUtcNow(),
                files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());
            await File.WriteAllTextAsync(Path.Combine(workDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken).ConfigureAwait(false);
            ValidateUncompressedWorkTree(workDirectory, cancellationToken);
            ZipFile.CreateFromDirectory(workDirectory, temporaryPackage, CompressionLevel.Optimal, false);
            ValidateArchiveResourceLimits(temporaryPackage, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPackage, packageFullPath, true);
            return manifest;
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
            TryDeleteFile(temporaryPackage);
        }
    }

    public async Task<IRestoreExecution> RestoreAsync(
        string packagePath,
        string databasePath,
        string documentsDirectory,
        int currentSchemaVersion,
        bool destructiveRestoreConfirmed,
        CancellationToken cancellationToken = default)
    {
        ValidateRestoreArguments(packagePath, databasePath, documentsDirectory, currentSchemaVersion,
            destructiveRestoreConfirmed);
        cancellationToken.ThrowIfCancellationRequested();

        string databaseFullPath = Path.GetFullPath(databasePath);
        string databaseDirectory = Path.GetDirectoryName(databaseFullPath)!;
        string documentsFullPath = TrimEndingSeparator(Path.GetFullPath(documentsDirectory));
        string documentsParent = Path.GetDirectoryName(documentsFullPath)!;
        Directory.CreateDirectory(databaseDirectory);
        Directory.CreateDirectory(documentsParent);
        string id = Guid.NewGuid().ToString("N");
        string extractionDirectory = Path.Combine(databaseDirectory, $".restore-{id}");
        string stagedDatabase = Path.Combine(databaseDirectory, $".{Path.GetFileName(databaseFullPath)}.restore-{id}");
        string safetyDatabase = Path.Combine(databaseDirectory, $"{Path.GetFileName(databaseFullPath)}.safety-{id}");
        string stagedDocuments = Path.Combine(documentsParent, $".{Path.GetFileName(documentsFullPath)}.restore-{id}");
        string safetyDocuments = Path.Combine(documentsParent, $"{Path.GetFileName(documentsFullPath)}.safety-{id}");
        string journalPath = GetRestoreJournalPath(databaseFullPath);
        Directory.CreateDirectory(extractionDirectory);

        bool targetExisted = File.Exists(databaseFullPath);
        bool documentsExisted = Directory.Exists(documentsFullPath);
        bool databaseInstalled = false;
        bool documentsMovedToSafety = false;
        bool documentsInstalled = false;
        bool recoveryFailed = false;
        bool replacementFailed = false;
        bool handedOffForApplicationValidation = false;
        try
        {
            await ExtractSecurelyAsync(packagePath, extractionDirectory, cancellationToken).ConfigureAwait(false);
            BackupManifest manifest = await ReadManifestAsync(
                Path.Combine(extractionDirectory, "manifest.json"), cancellationToken).ConfigureAwait(false);
            ValidateManifest(manifest, currentSchemaVersion);
            await ValidateFilesAsync(extractionDirectory, manifest, cancellationToken).ConfigureAwait(false);
            string restoredDatabase = Path.Combine(extractionDirectory, "database.sqlite");
            DatabaseIdentity identity = await ValidateDatabaseAsync(
                restoredDatabase, manifest.SchemaVersion, cancellationToken).ConfigureAwait(false);
            if (identity.UserVersion > currentSchemaVersion)
                throw new InvalidDataException("The backup was created by a newer database schema.");

            await CopyFileAsync(restoredDatabase, stagedDatabase, cancellationToken).ConfigureAwait(false);
            await ValidateDatabaseAsync(stagedDatabase, manifest.SchemaVersion, cancellationToken).ConfigureAwait(false);
            string restoredDocuments = Path.Combine(extractionDirectory, "documents");
            if (Directory.Exists(restoredDocuments))
                await CopyDirectoryAsync(restoredDocuments, stagedDocuments, cancellationToken).ConfigureAwait(false);
            else
                Directory.CreateDirectory(stagedDocuments);

            cancellationToken.ThrowIfCancellationRequested();
            await WriteRestoreJournalAsync(
                journalPath,
                new RestoreJournal(id, targetExisted, documentsExisted),
                cancellationToken).ConfigureAwait(false);
            SqliteConnection.ClearAllPools();
            DeleteSidecars(databaseFullPath);
            if (targetExisted)
                File.Replace(stagedDatabase, databaseFullPath, safetyDatabase, ignoreMetadataErrors: true);
            else
                File.Move(stagedDatabase, databaseFullPath);
            databaseInstalled = true;

            if (documentsExisted)
            {
                Directory.Move(documentsFullPath, safetyDocuments);
                documentsMovedToSafety = true;
            }
            Directory.Move(stagedDocuments, documentsFullPath);
            documentsInstalled = true;

            await ValidateDatabaseAsync(databaseFullPath, manifest.SchemaVersion, cancellationToken).ConfigureAwait(false);
            await VerifyDocumentsAsync(documentsFullPath, manifest, cancellationToken).ConfigureAwait(false);
            handedOffForApplicationValidation = true;
            return new RestoreExecution(
                databaseFullPath,
                documentsFullPath,
                stagedDatabase,
                stagedDocuments,
                safetyDatabase,
                safetyDocuments,
                journalPath,
                targetExisted,
                documentsExisted,
                currentSchemaVersion,
                id);
        }
        catch (Exception original)
        {
            replacementFailed = databaseInstalled || documentsMovedToSafety || documentsInstalled;
            if (!replacementFailed)
            {
                throw;
            }
            try
            {
                SqliteConnection.ClearAllPools();
                if (documentsInstalled && Directory.Exists(documentsFullPath))
                    Directory.Move(documentsFullPath, stagedDocuments);
                if (documentsMovedToSafety && Directory.Exists(safetyDocuments))
                    Directory.Move(safetyDocuments, documentsFullPath);

                if (databaseInstalled)
                {
                    DeleteSidecars(databaseFullPath);
                    if (targetExisted && File.Exists(safetyDatabase))
                        File.Replace(safetyDatabase, databaseFullPath, stagedDatabase, ignoreMetadataErrors: true);
                    else if (!targetExisted && File.Exists(databaseFullPath))
                        File.Move(databaseFullPath, stagedDatabase);
                }

                if (targetExisted)
                    await ValidateDatabaseAsync(databaseFullPath, currentSchemaVersion, CancellationToken.None)
                        .ConfigureAwait(false);
            }
            catch (Exception recovery)
            {
                recoveryFailed = true;
                throw new RestoreExecutionException(
                    "Restore failed and automatic recovery also failed. Safety artifacts were preserved.",
                    new AggregateException(original, recovery),
                    CreateRecoveryMetadata(RestorePhase.Rollback));
            }
            throw new RestoreExecutionException(
                "Restore failed and the original state was restored.",
                original,
                CreateRecoveryMetadata(RestorePhase.Replaced));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!recoveryFailed)
            {
                TryDeleteDirectory(extractionDirectory);
                TryDeleteDirectory(stagedDocuments);
                TryDeleteFile(stagedDatabase);
            }
            if (!handedOffForApplicationValidation && !recoveryFailed && !replacementFailed)
            {
                TryDeleteFile(safetyDatabase);
                DeleteSidecars(safetyDatabase);
                TryDeleteDirectory(safetyDocuments);
            }
            if (!handedOffForApplicationValidation && !recoveryFailed)
                TryDeleteFile(journalPath);
        }

        RestoreRecoveryMetadata CreateRecoveryMetadata(RestorePhase phase) => new(
            phase,
            File.Exists(stagedDatabase) ? stagedDatabase : null,
            Directory.Exists(stagedDocuments) ? stagedDocuments : null,
            File.Exists(safetyDatabase) ? safetyDatabase : null,
            Directory.Exists(safetyDocuments) ? safetyDocuments : null);
    }

    private sealed class RestoreExecution(
        string databasePath,
        string documentsPath,
        string stagedDatabasePath,
        string stagedDocumentsPath,
        string safetyDatabasePath,
        string safetyDocumentsPath,
        string journalPath,
        bool databaseExisted,
        bool documentsExisted,
        int schemaVersion,
        string restoreExecutionJournalId) : IRestoreExecution
    {
        private const int Active = 0;
        private const int Transitioning = 1;
        private const int Completed = 2;
        private int _state;

        public RestoreRecoveryMetadata Recovery => new(
            RestorePhase.Replaced,
            File.Exists(stagedDatabasePath) ? stagedDatabasePath : null,
            Directory.Exists(stagedDocumentsPath) ? stagedDocumentsPath : null,
            File.Exists(safetyDatabasePath) ? safetyDatabasePath : null,
            Directory.Exists(safetyDocumentsPath) ? safetyDocumentsPath : null);

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int priorState = Interlocked.CompareExchange(ref _state, Transitioning, Active);
            if (priorState == Completed)
                return;
            if (priorState != Active)
                throw new InvalidOperationException("A restore completion operation is already in progress.");

            try
            {
                SqliteConnection.ClearAllPools();
                await FlushRestoreGenerationAsync(
                    databasePath, documentsPath, cancellationToken).ConfigureAwait(false);
                // This durable marker is the commit point. Before it, rollback remains valid;
                // after it, startup recovery must preserve the complete new pair.
                await WriteRestoreJournalAsync(
                    journalPath,
                    new RestoreJournal(restoreExecutionJournalId, databaseExisted, documentsExisted,
                        RestoreCommitPhase.CommittingCleanup),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Volatile.Write(ref _state, Active);
                throw;
            }

            Volatile.Write(ref _state, Completed);

            TryDeleteFile(safetyDatabasePath);
            DeleteSidecars(safetyDatabasePath);
            TryDeleteDirectory(safetyDocumentsPath);
            TryDeleteFile(stagedDatabasePath);
            DeleteSidecars(stagedDatabasePath);
            TryDeleteDirectory(stagedDocumentsPath);
            if (!RestoreCleanupArtifactsExist(
                    stagedDatabasePath, stagedDocumentsPath, safetyDatabasePath, safetyDocumentsPath))
            {
                TryDeleteFile(journalPath);
            }
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int priorState = Interlocked.CompareExchange(ref _state, Transitioning, Active);
            if (priorState == Completed)
                return;
            if (priorState != Active)
                throw new InvalidOperationException("A restore completion operation is already in progress.");

            try
            {
                SqliteConnection.ClearAllPools();
                DeleteSidecars(databasePath);
                if (Directory.Exists(documentsPath))
                {
                    TryDeleteDirectory(stagedDocumentsPath);
                    Directory.Move(documentsPath, stagedDocumentsPath);
                }
                if (documentsExisted && Directory.Exists(safetyDocumentsPath))
                {
                    Directory.Move(safetyDocumentsPath, documentsPath);
                }
                else if (!documentsExisted)
                {
                    TryDeleteDirectory(documentsPath);
                }

                if (databaseExisted && File.Exists(safetyDatabasePath))
                {
                    TryDeleteFile(stagedDatabasePath);
                    File.Replace(safetyDatabasePath, databasePath, stagedDatabasePath, ignoreMetadataErrors: true);
                }
                else if (!databaseExisted)
                {
                    TryDeleteFile(databasePath);
                }

                if (databaseExisted)
                {
                    await ValidateDatabaseAsync(databasePath, schemaVersion, CancellationToken.None).ConfigureAwait(false);
                }

                TryDeleteFile(stagedDatabasePath);
                DeleteSidecars(stagedDatabasePath);
                TryDeleteDirectory(stagedDocumentsPath);
                TryDeleteFile(safetyDatabasePath);
                DeleteSidecars(safetyDatabasePath);
                TryDeleteDirectory(safetyDocumentsPath);
                TryDeleteFile(journalPath);
                Volatile.Write(ref _state, Completed);
            }
            catch
            {
                Volatile.Write(ref _state, Active);
                throw;
            }
        }
    }

    private static void ValidateCreateArguments(
        string databasePath, string documentsDirectory, string packagePath, int schemaVersion,
        string applicationVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("SQLite database was not found.", databasePath);
        RejectDangerousOverlap(databasePath, documentsDirectory, packagePath);
    }

    private static void ValidateRestoreArguments(
        string packagePath, string databasePath, string documentsDirectory, int currentSchemaVersion,
        bool destructiveRestoreConfirmed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentsDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(currentSchemaVersion);
        if (!destructiveRestoreConfirmed)
            throw new InvalidOperationException("Restore requires explicit destructive-operation confirmation.");
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Backup package was not found.", packagePath);
        RejectDangerousOverlap(databasePath, documentsDirectory, packagePath);
    }

    private static void RejectDangerousOverlap(string databasePath, string documentsDirectory, string packagePath)
    {
        string database = ResolvePath(databasePath);
        string documents = ResolvePath(documentsDirectory);
        string package = ResolvePath(packagePath);
        if (PathEquals(database, package) || IsWithin(database, documents) || IsWithin(documents, database) ||
            IsWithin(package, documents) || IsWithin(documents, package) ||
            PathEquals(database, documents) || PathEquals(package, documents))
            throw new ArgumentException("Database, document, and backup package paths must not overlap.");
    }

    private static string ResolvePath(string path)
    {
        string fullPath = TrimEndingSeparator(Path.GetFullPath(path));
        FileSystemInfo info = Directory.Exists(fullPath) ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        try { return TrimEndingSeparator(info.ResolveLinkTarget(true)?.FullName ?? fullPath); }
        catch (IOException) { return fullPath; }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsWithin(string candidate, string directory) =>
        candidate.StartsWith(TrimEndingSeparator(directory) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string TrimEndingSeparator(string path) => Path.TrimEndingDirectorySeparator(path);

    private static async Task OnlineBackupAsync(string sourcePath, string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using SqliteConnection source = new($"Data Source={sourcePath};Mode=ReadOnly;Pooling=False");
        await using SqliteConnection destination = new($"Data Source={destinationPath};Pooling=False");
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    private static async Task<DatabaseIdentity> ValidateDatabaseAsync(
        string databasePath, int expectedSchemaVersion, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            string integrity = Convert.ToString(await ScalarAsync(connection, "PRAGMA integrity_check;", cancellationToken)
                .ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The backup database failed SQLite integrity validation.");
            await using (SqliteCommand foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_key_check;";
                await using SqliteDataReader violations = await foreignKeys.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (await violations.ReadAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("The backup database contains foreign-key violations.");
            }

            int applicationId = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA application_id;", cancellationToken)
                .ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            int userVersion = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken)
                .ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (applicationId != ExpectedApplicationId)
                throw new InvalidDataException("The backup database has an unexpected application identifier.");
            if (userVersion <= 0 || userVersion != expectedSchemaVersion)
                throw new InvalidDataException("The database schema version does not match the requested or manifested version.");
            if (!SupportedSchemas.TryGetValue(userVersion, out SupportedSchema? supportedSchema))
                throw new InvalidDataException("The database schema version is unsupported.");

            HashSet<string> tables = await ReadNamesAsync(connection, "table", cancellationToken).ConfigureAwait(false);
            HashSet<string> triggers = await ReadNamesAsync(connection, "trigger", cancellationToken).ConfigureAwait(false);
            if (RequiredTables.Any(table => !tables.Contains(table)) ||
                RequiredTriggers.Any(trigger => !triggers.Contains(trigger)) ||
                (userVersion >= 3 &&
                 (!triggers.Contains("trg_invoice_voids_validate") ||
                  !triggers.Contains("trg_audit_events_validate_insert") ||
                  !triggers.Contains("trg_invoice_voids_create_audit"))))
                throw new InvalidDataException("The backup database is missing required core tables or triggers.");
            await using (SqliteCommand migrations = connection.CreateCommand())
            {
                migrations.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";
                await using SqliteDataReader reader = await migrations.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (string expectedMigration in supportedSchema.Migrations)
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                        !string.Equals(reader.GetString(0), expectedMigration, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("The backup database EF migration history is incomplete or unsupported.");
                    }
                }
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The backup database EF migration history is incomplete or unsupported.");
                }
            }
            string schemaFingerprint = await ReadSchemaFingerprintAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(schemaFingerprint, supportedSchema.Fingerprint, StringComparison.Ordinal))
                throw new InvalidDataException("The backup database schema fingerprint is unsupported.");
            await ValidateIssuanceLedgerAsync(databasePath, cancellationToken).ConfigureAwait(false);
            return new DatabaseIdentity(applicationId, userVersion);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The embedded backup database is invalid.", exception);
        }
    }

    internal static async Task ValidateSupportedCurrentDatabaseAsync(
        string databasePath,
        int expectedSchemaVersion,
        CancellationToken cancellationToken)
    {
        _ = await ValidateDatabaseAsync(databasePath, expectedSchemaVersion, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ValidateIssuanceLedgerAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        string validationPath = $"{databasePath}.ledger-{Guid.NewGuid():N}";
        try
        {
            File.Copy(databasePath, validationPath, overwrite: false);
            DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
                .UseSqlite($"Data Source={validationPath};Pooling=False")
                .Options;
            await using (MhcDbContext context = new(options))
            {
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            }

            await using SqliteConnection connection = new($"Data Source={validationPath};Pooling=False");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            long invoices = Convert.ToInt64(
                await ScalarAsync(connection, "SELECT COUNT(*) FROM invoices;", cancellationToken)
                    .ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            long finalizations = Convert.ToInt64(
                await ScalarAsync(connection, "SELECT COUNT(*) FROM invoice_finalizations;", cancellationToken)
                    .ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (invoices != finalizations)
                throw new InvalidDataException("The backup database contains incomplete issuance records.");

            await using (SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                await ExecuteValidationSqlAsync(
                connection,
                transaction,
                """
                INSERT OR IGNORE INTO invoice_finalizations (invoice_id, finalized_at_utc_ms)
                SELECT invoice_id, finalized_at_utc_ms FROM invoice_finalizations;
                INSERT OR IGNORE INTO invoice_voids (invoice_id, reason, voided_at_utc_ms, operator_name)
                SELECT invoice_id, reason, voided_at_utc_ms, operator_name FROM invoice_voids;
                """,
                    cancellationToken).ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            long invalidVoids = Convert.ToInt64(await ScalarAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM invoice_voids AS v
                WHERE (SELECT COUNT(*) FROM audit_events AS ae
                       WHERE ae.invoice_id = v.invoice_id AND ae.event_type = 3) <> 1
                   OR NOT EXISTS (
                       SELECT 1 FROM audit_events AS ae
                       WHERE ae.invoice_id = v.invoice_id
                         AND ae.event_type = 3
                         AND ae.occurred_at_utc_ms = v.voided_at_utc_ms
                         AND ae.operator_name = v.operator_name
                         AND json_valid(ae.details_json)
                         AND json_type(ae.details_json, '$') = 'object'
                         AND json_extract(ae.details_json, '$.reason') = v.reason);
                """,
                cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (invalidVoids != 0)
                throw new InvalidDataException("The backup database contains invalid void audit evidence.");

            long orphanVoidAudits = Convert.ToInt64(await ScalarAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM audit_events AS ae
                WHERE ae.event_type = 3
                  AND NOT EXISTS (
                      SELECT 1 FROM invoice_voids AS v
                      WHERE v.invoice_id = ae.invoice_id
                        AND v.voided_at_utc_ms = ae.occurred_at_utc_ms
                        AND v.operator_name = ae.operator_name
                        AND json_valid(ae.details_json)
                        AND json_type(ae.details_json, '$') = 'object'
                        AND json_extract(ae.details_json, '$.reason') = v.reason);
                """,
                cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            if (orphanVoidAudits != 0)
                throw new InvalidDataException("The backup database contains orphan void audit evidence.");


            await ValidateDocumentHashesAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or DbUpdateException)
        {
            throw new InvalidDataException("The backup database issuance ledger is invalid.", exception);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(validationPath);
            DeleteSidecars(validationPath);
        }
    }

    private static async Task ExecuteValidationSqlAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateDocumentHashesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT pdf_bytes, sha256, byte_length FROM invoice_documents;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            byte[] pdfBytes = reader.GetFieldValue<byte[]>(0);
            byte[] storedHash = reader.GetFieldValue<byte[]>(1);
            long declaredLength = reader.GetInt64(2);
            byte[] computedHash = SHA256.HashData(pdfBytes);
            if (declaredLength != pdfBytes.LongLength || storedHash.Length != computedHash.Length ||
                !CryptographicOperations.FixedTimeEquals(storedHash, computedHash))
            {
                throw new InvalidDataException("A canonical PDF failed cryptographic integrity validation.");
            }
        }
    }

    private static async Task<string> ReadSchemaFingerprintAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT type, name, tbl_name, COALESCE(sql, '') FROM sqlite_schema " +
            "WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            for (int index = 0; index < 4; index++)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(reader.GetString(index)));
                hash.AppendData([0]);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<HashSet<string>> ReadNamesAsync(
        SqliteConnection connection, string type, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type=$type;";
        command.Parameters.AddWithValue("$type", type);
        HashSet<string> names = new(StringComparer.Ordinal);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ValidateUncompressedWorkTree(string workDirectory, CancellationToken cancellationToken)
    {
        FileInfo[] files = Directory.EnumerateFiles(workDirectory, "*", SearchOption.AllDirectories)
            .Select(static path => new FileInfo(path))
            .ToArray();
        if (files.Length > _resourceLimits.MaxEntryCount)
            throw new InvalidDataException("The backup contains too many entries.");

        long total = 0;
        foreach (FileInfo file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Length > _resourceLimits.MaxEntryUncompressedBytes)
                throw new InvalidDataException("A backup entry exceeds the uncompressed size limit.");
            try { total = checked(total + file.Length); }
            catch (OverflowException exception) { throw new InvalidDataException("Backup size is invalid.", exception); }
            if (total > _resourceLimits.MaxTotalUncompressedBytes)
                throw new InvalidDataException("The backup exceeds the total uncompressed size limit.");
        }
    }

    private void ValidateArchiveResourceLimits(string packagePath, CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        ValidateArchiveResourceLimits(archive, cancellationToken);
    }

    private void ValidateArchiveResourceLimits(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > _resourceLimits.MaxEntryCount)
            throw new InvalidDataException("The backup package contains too many entries.");
        long declaredTotal = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length > _resourceLimits.MaxEntryUncompressedBytes ||
                entry.Length > _resourceLimits.MaxCompressionRatio * Math.Max(1L, entry.CompressedLength))
                throw new InvalidDataException("A backup entry exceeds resource or compression limits.");
            try { declaredTotal = checked(declaredTotal + entry.Length); }
            catch (OverflowException exception) { throw new InvalidDataException("Backup size is invalid.", exception); }
            if (declaredTotal > _resourceLimits.MaxTotalUncompressedBytes)
                throw new InvalidDataException("The backup package exceeds the total extraction limit.");
        }
    }

    internal static async Task<long> CopyBoundedAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return total;
            if (total > maximumBytes - read)
                throw new InvalidDataException("A backup entry expanded beyond its actual-byte resource limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }
    }

    private async Task ExtractSecurelyAsync(
        string packagePath, string destinationDirectory, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        long actualTotal = 0;
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        ValidateArchiveResourceLimits(archive, cancellationToken);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedName = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, normalizedName));
            if (!destinationPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !paths.Add(destinationPath))
                throw new InvalidDataException("The backup package contains an invalid or duplicate path.");
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            long remainingTotal = _resourceLimits.MaxTotalUncompressedBytes - actualTotal;
            double ratioBytes = _resourceLimits.MaxCompressionRatio * Math.Max(1L, entry.CompressedLength);
            long ratioLimit = ratioBytes >= long.MaxValue ? long.MaxValue : (long)Math.Floor(ratioBytes);
            long actualLimit = Math.Min(
                _resourceLimits.MaxEntryUncompressedBytes,
                Math.Min(remainingTotal, ratioLimit));
            await using Stream input = entry.Open();
            await using FileStream output = new(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long actualLength = await CopyBoundedAsync(input, output, actualLimit, cancellationToken).ConfigureAwait(false);
            actualTotal = checked(actualTotal + actualLength);
            if (actualLength != entry.Length)
                throw new InvalidDataException("A backup entry length does not match its archive declaration.");
        }
    }

    private static async Task<BackupManifest> ReadManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath)) throw new InvalidDataException("The backup manifest is missing.");
        await using FileStream stream = File.OpenRead(manifestPath);
        try
        {
            return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The backup manifest is invalid.");
        }
        catch (JsonException exception) { throw new InvalidDataException("The backup manifest is invalid.", exception); }
    }

    private static void ValidateManifest(BackupManifest manifest, int currentSchemaVersion)
    {
        if (manifest.FormatVersion != CurrentFormatVersion || manifest.SchemaVersion <= 0 ||
            string.IsNullOrWhiteSpace(manifest.ApplicationVersion) || manifest.Files is null || manifest.Files.Count == 0)
            throw new InvalidDataException("The backup manifest is invalid or unsupported.");
        if (manifest.SchemaVersion > currentSchemaVersion)
            throw new InvalidDataException("The backup was created by a newer database schema.");
    }

    private static async Task ValidateFilesAsync(
        string extractionDirectory, BackupManifest manifest, CancellationToken cancellationToken)
    {
        HashSet<string> expectedPaths = manifest.Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        if (!expectedPaths.Contains("database.sqlite") || expectedPaths.Count != manifest.Files.Count)
            throw new InvalidDataException("The backup manifest contains duplicate or missing file entries.");
        string root = Path.GetFullPath(extractionDirectory) + Path.DirectorySeparatorChar;
        foreach (BackupFileEntry expected in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(expected.Path) || expected.Length < 0 || expected.Sha256?.Length != 64)
                throw new InvalidDataException("The backup manifest contains an invalid file entry.");
            string filePath = Path.GetFullPath(Path.Combine(extractionDirectory,
                expected.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
                throw new InvalidDataException("A backup file is missing or has an invalid path.");
            FileInfo info = new(filePath);
            if (info.Length != expected.Length)
                throw new InvalidDataException("A backup file length does not match its manifest.");
            await using FileStream stream = File.OpenRead(filePath);
            byte[] actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            byte[] claimed;
            try { claimed = Convert.FromHexString(expected.Sha256); }
            catch (FormatException exception) { throw new InvalidDataException("A backup hash is invalid.", exception); }
            if (!CryptographicOperations.FixedTimeEquals(actual, claimed))
                throw new InvalidDataException("A backup file hash does not match its manifest.");
        }
        HashSet<string> actualPaths = Directory.EnumerateFiles(extractionDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(extractionDirectory, path).Replace('\\', '/'))
            .Where(path => path != "manifest.json").ToHashSet(StringComparer.Ordinal);
        if (!actualPaths.SetEquals(expectedPaths))
            throw new InvalidDataException("The backup package contains unexpected files.");
    }

    private static async Task VerifyDocumentsAsync(
        string documentsDirectory, BackupManifest manifest, CancellationToken cancellationToken)
    {
        foreach (BackupFileEntry file in manifest.Files.Where(file => file.Path.StartsWith("documents/", StringComparison.Ordinal)))
        {
            string path = Path.Combine(documentsDirectory, file.Path[10..].Replace('/', Path.DirectorySeparatorChar));
            await using FileStream stream = File.OpenRead(path);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (stream.Length != file.Length || !CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(file.Sha256)))
                throw new InvalidDataException("Restored documents failed verification.");
        }
    }

    private static async Task<BackupFileEntry> CreateFileEntryAsync(
        string filePath, string archivePath, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(filePath);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new BackupFileEntry(archivePath, Convert.ToHexString(hash).ToLowerInvariant(), stream.Length);
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task FlushRestoreGenerationAsync(
        string databasePath,
        string documentsPath,
        CancellationToken cancellationToken)
    {
        await FlushFileToDiskAsync(databasePath, cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(documentsPath))
            return;

        foreach (string documentPath in Directory.EnumerateFiles(documentsPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await FlushFileToDiskAsync(documentPath, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task FlushFileToDiskAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static bool RestoreCleanupArtifactsExist(
        string stagedDatabase,
        string stagedDocuments,
        string safetyDatabase,
        string safetyDocuments) =>
        File.Exists(stagedDatabase) ||
        File.Exists(stagedDatabase + "-wal") ||
        File.Exists(stagedDatabase + "-shm") ||
        Directory.Exists(stagedDocuments) ||
        File.Exists(safetyDatabase) ||
        File.Exists(safetyDatabase + "-wal") ||
        File.Exists(safetyDatabase + "-shm") ||
        Directory.Exists(safetyDocuments);

    private static async Task CopyDirectoryAsync(
        string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CopyFileAsync(sourcePath,
                Path.Combine(destinationDirectory, Path.GetRelativePath(sourceDirectory, sourcePath)), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void DeleteSidecars(string databasePath)
    {
        foreach (string suffix in new[] { "-wal", "-shm" }) TryDeleteFile(databasePath + suffix);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private sealed record DatabaseIdentity(int ApplicationId, int UserVersion);
}

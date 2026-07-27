namespace MHC.Invoicing.Application.Maintenance;

public sealed record RestoreMaintenanceRequest(
    string PackagePath,
    string DatabasePath,
    string DocumentsDirectory,
    int CurrentSchemaVersion,
    bool DestructiveRestoreConfirmed);

public interface IRestoreExecutor
{
    Task<IRestoreExecution> RestoreAsync(
        RestoreMaintenanceRequest request,
        CancellationToken cancellationToken = default);
}

public enum RestorePhase
{
    None,
    Staged,
    Replaced,
    Reopen,
    Rollback,
    Cleanup,
}

public sealed record RestoreRecoveryMetadata(
    RestorePhase Phase,
    string? StagedDatabasePath = null,
    string? StagedDocumentsPath = null,
    string? SafetyDatabasePath = null,
    string? SafetyDocumentsPath = null)
{
    public static RestoreRecoveryMetadata Empty { get; } = new(RestorePhase.None);

    public IReadOnlyList<string> RetainedPaths =>
        new[] { StagedDatabasePath, StagedDocumentsPath, SafetyDatabasePath, SafetyDocumentsPath }
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

public interface IRestoreExecution
{
    RestoreRecoveryMetadata Recovery { get; }

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

public sealed class RestoreExecutionException : Exception
{
    public RestoreExecutionException(
        string message,
        Exception innerException,
        RestoreRecoveryMetadata recovery)
        : base(message, innerException)
    {
        Recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
    }

    public RestoreRecoveryMetadata Recovery { get; }
}

public interface IRestoreMaintenanceLeaseProvider
{
    ValueTask<IRestoreMaintenanceLease> AcquireAsync(CancellationToken cancellationToken = default);
}

public interface IRestoreMaintenanceLease : IAsyncDisposable
{
    Task QuiesceOwnedContextsAsync(CancellationToken cancellationToken = default);

    Task ReopenAndValidateAsync(CancellationToken cancellationToken = default);

    Task ResumeAsync(CancellationToken cancellationToken = default);
}

public interface ISqlitePoolMaintenance
{
    void ClearAllPools();
}


public interface IApplicationWorkGate
{
    ValueTask<IAsyncDisposable> EnterWorkAsync(CancellationToken cancellationToken = default);
}

public sealed class ApplicationMaintenanceGate :
    IApplicationWorkGate,
    IRestoreMaintenanceLeaseProvider,
    IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Func<CancellationToken, Task> _quiesceOwnedContexts;
    private Func<CancellationToken, Task> _reopenAndValidate;
    private bool _disposed;

    public ApplicationMaintenanceGate(
        Func<CancellationToken, Task> quiesceOwnedContexts,
        Func<CancellationToken, Task> reopenAndValidate)
    {
        _quiesceOwnedContexts = quiesceOwnedContexts ?? throw new ArgumentNullException(nameof(quiesceOwnedContexts));
        _reopenAndValidate = reopenAndValidate ?? throw new ArgumentNullException(nameof(reopenAndValidate));
    }

    public static ApplicationMaintenanceGate Shared { get; } = new(
        _ => Task.CompletedTask,
        _ => Task.CompletedTask);

    public void Configure(
        Func<CancellationToken, Task> quiesceOwnedContexts,
        Func<CancellationToken, Task> reopenAndValidate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _quiesceOwnedContexts = quiesceOwnedContexts ?? throw new ArgumentNullException(nameof(quiesceOwnedContexts));
        _reopenAndValidate = reopenAndValidate ?? throw new ArgumentNullException(nameof(reopenAndValidate));
    }

    public async ValueTask<IAsyncDisposable> EnterWorkAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_gate);
    }

    public async ValueTask<IRestoreMaintenanceLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new MaintenanceLease(_gate, _quiesceOwnedContexts, _reopenAndValidate);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class MaintenanceLease(
        SemaphoreSlim gate,
        Func<CancellationToken, Task> quiesce,
        Func<CancellationToken, Task> reopen) : IRestoreMaintenanceLease
    {
        private int _released;

        public Task QuiesceOwnedContextsAsync(CancellationToken cancellationToken = default) =>
            quiesce(cancellationToken);

        public Task ReopenAndValidateAsync(CancellationToken cancellationToken = default) =>
            reopen(cancellationToken);

        public Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Release();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }

        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}

public sealed class RestoreMaintenanceException : Exception
{
    public RestoreMaintenanceException(
        string message,
        Exception restoreFailure,
        RestoreRecoveryMetadata recovery,
        IReadOnlyList<Exception> maintenanceFailures)
        : base(message, restoreFailure)
    {
        Recovery = recovery;
        MaintenanceFailures = maintenanceFailures;
    }

    public RestoreRecoveryMetadata Recovery { get; }

    public IReadOnlyList<string> RetainedRecoveryArtifacts => Recovery.RetainedPaths;

    public IReadOnlyList<Exception> MaintenanceFailures { get; }
}

public sealed class RestoreMaintenanceCoordinator(
    IRestoreMaintenanceLeaseProvider maintenance,
    IRestoreExecutor restoreExecutor,
    ISqlitePoolMaintenance pools)
{
    public async Task RestoreAsync(
        RestoreMaintenanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.DestructiveRestoreConfirmed)
        {
            throw new InvalidOperationException("Restore requires explicit destructive-operation confirmation.");
        }

        await using IRestoreMaintenanceLease lease = await maintenance.AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await lease.QuiesceOwnedContextsAsync(cancellationToken).ConfigureAwait(false);
        pools.ClearAllPools();
        IRestoreExecution? execution = null;
        RestorePhase failurePhase = RestorePhase.None;
        try
        {
            execution = await restoreExecutor.RestoreAsync(request, cancellationToken).ConfigureAwait(false);
            pools.ClearAllPools();
            failurePhase = RestorePhase.Reopen;
            await lease.ReopenAndValidateAsync(cancellationToken).ConfigureAwait(false);
            failurePhase = RestorePhase.Cleanup;
            await execution.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            await lease.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception restoreFailure)
        {
            List<Exception> maintenanceFailures = [];
            RestoreRecoveryMetadata recovery = execution?.Recovery ??
                (restoreFailure as RestoreExecutionException)?.Recovery ??
                RestoreRecoveryMetadata.Empty;

            if (execution is not null &&
                failurePhase is RestorePhase.Reopen or RestorePhase.Cleanup)
            {
                try
                {
                    await execution.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    maintenanceFailures.Add(failure);
                    recovery = recovery with { Phase = RestorePhase.Rollback };
                }
            }

            try
            {
                pools.ClearAllPools();
            }
            catch (Exception failure)
            {
                maintenanceFailures.Add(failure);
            }

            try
            {
                await lease.ReopenAndValidateAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                maintenanceFailures.Add(failure);
            }

            try
            {
                await lease.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                maintenanceFailures.Add(failure);
            }

            if (recovery.Phase != RestorePhase.Rollback)
            {
                recovery = recovery with { Phase = failurePhase };
            }
            Exception primaryFailure = restoreFailure is RestoreExecutionException executionFailure
                ? executionFailure.InnerException ?? executionFailure
                : restoreFailure;
            throw new RestoreMaintenanceException(
                "Restore failed. The application attempted to reopen the current database and retained recovery artifacts are listed.",
                primaryFailure,
                recovery,
                maintenanceFailures);
        }
    }
}

using MHC.Invoicing.Application.Maintenance;

namespace MHC.Invoicing.Application.Tests.Maintenance;

public sealed class RestoreMaintenanceCoordinatorTests
{
    [Fact]
    public async Task Restore_requires_explicit_destructive_confirmation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingMaintenance maintenance = new();
        RestoreMaintenanceCoordinator coordinator = Create(maintenance, new RecordingRestore(maintenance.Events), new RecordingPools(maintenance.Events));

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RestoreAsync(Request(confirmed: false), token));

        Assert.Empty(maintenance.Events);
    }

    [Fact]
    public async Task Restore_quiesces_and_clears_pools_before_replacement_then_reopens_before_resume()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingMaintenance maintenance = new();
        RestoreMaintenanceCoordinator coordinator = Create(maintenance, new RecordingRestore(maintenance.Events), new RecordingPools(maintenance.Events));

        await coordinator.RestoreAsync(Request(confirmed: true), token);

        Assert.Equal(
            ["acquire", "quiesce", "pools", "restore", "pools", "reopen", "commit", "resume", "dispose"],
            maintenance.Events);
    }

    [Fact]
    public async Task Reopen_failure_rolls_back_before_reopening_original_state_and_preserves_structured_artifacts()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingMaintenance maintenance = new(reopenFailures: 1);
        RecordingRestore restore = new(maintenance.Events);
        RestoreMaintenanceCoordinator coordinator = Create(
            maintenance,
            restore,
            new RecordingPools(maintenance.Events));

        RestoreMaintenanceException exception = await Assert.ThrowsAsync<RestoreMaintenanceException>(
            () => coordinator.RestoreAsync(Request(confirmed: true), token));

        Assert.Equal(RestorePhase.Reopen, exception.Recovery.Phase);
        Assert.Equal("safety.db", exception.Recovery.SafetyDatabasePath);
        Assert.Equal(
            ["acquire", "quiesce", "pools", "restore", "pools", "reopen", "rollback", "pools", "reopen", "resume", "dispose"],
            maintenance.Events);
    }

    [Fact]
    public async Task Precommit_cleanup_failure_rolls_back_before_reopening_original_state()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingMaintenance maintenance = new();
        RestoreMaintenanceCoordinator coordinator = Create(
            maintenance,
            new FailOnCommitRestore(maintenance.Events),
            new RecordingPools(maintenance.Events));

        RestoreMaintenanceException exception = await Assert.ThrowsAsync<RestoreMaintenanceException>(
            () => coordinator.RestoreAsync(Request(confirmed: true), token));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Equal(RestorePhase.Cleanup, exception.Recovery.Phase);
        Assert.Equal(
            ["acquire", "quiesce", "pools", "restore", "pools", "reopen", "commit", "rollback", "pools", "reopen", "resume", "dispose"],
            maintenance.Events);
    }

    [Fact]
    public async Task Restore_failure_attempts_reopen_and_resume_and_reports_recovery_artifacts()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingMaintenance maintenance = new();
        InvalidDataException failure = new("invalid backup");
        RestoreMaintenanceCoordinator coordinator = Create(
            maintenance,
            new RecordingRestore(maintenance.Events, failure),
            new RecordingPools(maintenance.Events));

        RestoreMaintenanceException exception = await Assert.ThrowsAsync<RestoreMaintenanceException>(
            () => coordinator.RestoreAsync(Request(confirmed: true), token));

        Assert.Same(failure, exception.InnerException);
        Assert.Equal(["safety.db"], exception.RetainedRecoveryArtifacts);
        Assert.Equal(
            ["acquire", "quiesce", "pools", "restore", "pools", "reopen", "resume", "dispose"],
            maintenance.Events);
    }

    [Fact]
    public async Task Cancellation_after_maintenance_starts_still_reopens_and_resumes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        RecordingMaintenance maintenance = new();
        RestoreMaintenanceCoordinator coordinator = Create(
            maintenance,
            new RecordingRestore(maintenance.Events, new OperationCanceledException()),
            new RecordingPools(maintenance.Events));

        await Assert.ThrowsAsync<RestoreMaintenanceException>(() => coordinator.RestoreAsync(Request(confirmed: true), token));

        Assert.Contains("reopen", maintenance.Events);
        Assert.Contains("resume", maintenance.Events);
    }

    [Fact]
    public async Task Cancellation_after_commit_still_resumes_and_returns_success()
    {
        using CancellationTokenSource cancellation = new();
        RecordingMaintenance maintenance = new();
        RestoreMaintenanceCoordinator coordinator = Create(
            maintenance,
            new CancelOnCommitRestore(maintenance.Events, cancellation),
            new RecordingPools(maintenance.Events));

        await coordinator.RestoreAsync(Request(confirmed: true), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(
            ["acquire", "quiesce", "pools", "restore", "pools", "reopen", "commit", "resume", "dispose"],
            maintenance.Events);
    }

    [Fact]
    public async Task Maintenance_gate_blocks_new_work_until_maintenance_resumes()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using ApplicationMaintenanceGate gate = new(_ => Task.CompletedTask, _ => Task.CompletedTask);
        await using IRestoreMaintenanceLease lease = await gate.AcquireAsync(token);
        Task<IAsyncDisposable> pendingWork = gate.EnterWorkAsync(token).AsTask();

        Assert.False(pendingWork.IsCompleted);

        await lease.ResumeAsync(token);
        await using IAsyncDisposable work = await pendingWork.WaitAsync(TimeSpan.FromSeconds(2), token);
    }

    private static RestoreMaintenanceCoordinator Create(
        RecordingMaintenance maintenance,
        IRestoreExecutor restore,
        ISqlitePoolMaintenance pools) =>
        new(maintenance, restore, pools);

    private static RestoreMaintenanceRequest Request(bool confirmed) =>
        new("backup.mhcbak", "current.db", "documents", 1, confirmed);

    private sealed class RecordingMaintenance(int reopenFailures = 0) : IRestoreMaintenanceLeaseProvider
    {
        public List<string> Events { get; } = [];

        public ValueTask<IRestoreMaintenanceLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("acquire");
            return ValueTask.FromResult<IRestoreMaintenanceLease>(new RecordingLease(Events, reopenFailures));
        }
    }

    private sealed class RecordingLease(List<string> events, int reopenFailures = 0) : IRestoreMaintenanceLease
    {
        public Task QuiesceOwnedContextsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("quiesce");
            return Task.CompletedTask;
        }

        public Task ReopenAndValidateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("reopen");
            if (Interlocked.Decrement(ref reopenFailures) >= 0)
            {
                throw new InvalidDataException("reopen failed");
            }
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("resume");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            events.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingRestore(List<string> events, Exception? failure = null) : IRestoreExecutor
    {
        public Task<IRestoreExecution> RestoreAsync(RestoreMaintenanceRequest request, CancellationToken cancellationToken = default)
        {
            events.Add("restore");
            return failure is null
                ? Task.FromResult<IRestoreExecution>(new RecordingExecution(events))
                : Task.FromException<IRestoreExecution>(new RestoreExecutionException(
                    "replace failed",
                    failure,
                    new RestoreRecoveryMetadata(RestorePhase.Replaced, SafetyDatabasePath: "safety.db")));
        }
    }

    private sealed class FailOnCommitRestore(List<string> events) : IRestoreExecutor
    {
        public Task<IRestoreExecution> RestoreAsync(
            RestoreMaintenanceRequest request,
            CancellationToken cancellationToken = default)
        {
            events.Add("restore");
            return Task.FromResult<IRestoreExecution>(new FailOnCommitExecution(events));
        }
    }

    private sealed class FailOnCommitExecution(List<string> events) : IRestoreExecution
    {
        public RestoreRecoveryMetadata Recovery { get; } = new(RestorePhase.Replaced);

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            events.Add("commit");
            return Task.FromException(new IOException("durable marker failed"));
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            events.Add("rollback");
            return Task.CompletedTask;
        }
    }

    private sealed class CancelOnCommitRestore(
        List<string> events,
        CancellationTokenSource cancellation) : IRestoreExecutor
    {
        public Task<IRestoreExecution> RestoreAsync(
            RestoreMaintenanceRequest request,
            CancellationToken cancellationToken = default)
        {
            events.Add("restore");
            return Task.FromResult<IRestoreExecution>(new CancelOnCommitExecution(events, cancellation));
        }
    }

    private sealed class CancelOnCommitExecution(
        List<string> events,
        CancellationTokenSource cancellation) : IRestoreExecution
    {
        public RestoreRecoveryMetadata Recovery { get; } = new(RestorePhase.Replaced);

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            events.Add("commit");
            cancellation.Cancel();
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            events.Add("rollback");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExecution(List<string> events) : IRestoreExecution
    {
        public RestoreRecoveryMetadata Recovery { get; } = new(
            RestorePhase.Replaced,
            "staged.db",
            "staged-documents",
            "safety.db",
            "safety-documents");

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("commit");
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            events.Add("rollback");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPools(List<string> events) : ISqlitePoolMaintenance
    {
        public void ClearAllPools() => events.Add("pools");
    }

}

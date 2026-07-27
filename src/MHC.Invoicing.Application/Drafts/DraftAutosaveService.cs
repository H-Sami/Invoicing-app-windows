using MHC.Invoicing.Application.Persistence;

namespace MHC.Invoicing.Application.Drafts;

public enum DraftAutosaveStatus
{
    Saved,
    Conflict,
}

public sealed record DraftAutosaveResult(
    DraftAutosaveStatus Status,
    VersionedDraft? SavedDraft,
    VersionedDraft? CurrentDraft);

public interface ITransientPersistenceErrorPolicy
{
    bool IsTransient(Exception exception);
}

public sealed class DraftAutosaveService
{
    private readonly TimeSpan _debounceDelay;
    private readonly int _maximumAttempts;
    private readonly IDraftRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly ITransientPersistenceErrorPolicy _transientErrorPolicy;

    public DraftAutosaveService(
        IDraftRepository repository,
        ITransientPersistenceErrorPolicy transientErrorPolicy,
        TimeProvider? timeProvider = null,
        TimeSpan? debounceDelay = null,
        int maximumAttempts = 3)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(transientErrorPolicy);
        if (debounceDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounceDelay));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);

        _repository = repository;
        _transientErrorPolicy = transientErrorPolicy;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _debounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(600);
        _maximumAttempts = maximumAttempts;
    }

    public async Task<DraftAutosaveResult> SaveAfterDebounceAsync(
        DraftRecord draft,
        int? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await Task.Delay(_debounceDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                VersionedDraft saved = await _repository.SaveAsync(
                    draft,
                    expectedRevision,
                    cancellationToken).ConfigureAwait(false);
                return new DraftAutosaveResult(DraftAutosaveStatus.Saved, saved, null);
            }
            catch (PersistenceConcurrencyException)
            {
                VersionedDraft? current = await _repository.GetAsync(draft.Id, cancellationToken).ConfigureAwait(false);
                return new DraftAutosaveResult(DraftAutosaveStatus.Conflict, null, current);
            }
            catch (Exception exception) when (
                attempt < _maximumAttempts &&
                _transientErrorPolicy.IsTransient(exception))
            {
                TimeSpan retryDelay = TimeSpan.FromMilliseconds(100 * attempt);
                await Task.Delay(retryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

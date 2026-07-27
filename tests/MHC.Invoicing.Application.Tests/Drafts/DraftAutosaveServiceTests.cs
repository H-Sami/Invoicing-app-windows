using MHC.Invoicing.Application.Drafts;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;

namespace MHC.Invoicing.Application.Tests.Drafts;

public sealed class DraftAutosaveServiceTests
{
    [Fact]
    public async Task CancellationDuringDebounceDoesNotPersist()
    {
        FakeDraftRepository repository = new(CreateDraft());
        DraftAutosaveService service = new(
            repository,
            new NoTransientErrors(),
            debounceDelay: TimeSpan.FromMinutes(1));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SaveAfterDebounceAsync(CreateDraft(), null, cancellation.Token));

        Assert.Equal(0, repository.SaveAttempts);
    }

    [Fact]
    public async Task TransientFailureIsRetriedWithoutAllocatingAnyPublicNumber()
    {
        DraftRecord draft = CreateDraft();
        FakeDraftRepository repository = new(draft) { TransientFailuresRemaining = 1 };
        DraftAutosaveService service = new(
            repository,
            new IoIsTransient(),
            debounceDelay: TimeSpan.Zero);

        DraftAutosaveResult result = await service.SaveAfterDebounceAsync(
            draft,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(DraftAutosaveStatus.Saved, result.Status);
        Assert.Equal(2, repository.SaveAttempts);
        Assert.Equal(0, result.SavedDraft!.Revision);
    }

    [Fact]
    public async Task RevisionConflictReturnsCurrentDraftWithoutOverwritingIt()
    {
        DraftRecord current = CreateDraft();
        FakeDraftRepository repository = new(current) { ThrowConcurrency = true };
        DraftAutosaveService service = new(
            repository,
            new NoTransientErrors(),
            debounceDelay: TimeSpan.Zero);

        DraftAutosaveResult result = await service.SaveAfterDebounceAsync(
            current with { Notes = "stale edit" },
            3,
            TestContext.Current.CancellationToken);

        Assert.Equal(DraftAutosaveStatus.Conflict, result.Status);
        Assert.Same(current, result.CurrentDraft!.Draft);
        Assert.Null(result.SavedDraft);
    }

    private static DraftRecord CreateDraft()
    {
        DateTimeOffset now = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);
        return new DraftRecord(
            Guid.CreateVersion7(),
            InvoiceDocumentType.TaxInvoice,
            null,
            null,
            new DateOnly(2026, 7, 23),
            new DraftParty("عميل نقدي", null, null, null, null),
            PaymentMethod.Cash,
            null,
            null,
            false,
            [],
            now,
            now);
    }

    private sealed class FakeDraftRepository(DraftRecord current) : IDraftRepository
    {
        public int SaveAttempts { get; private set; }

        public int TransientFailuresRemaining { get; set; }

        public bool ThrowConcurrency { get; set; }

        public Task<VersionedDraft> SaveAsync(
            DraftRecord draft,
            int? expectedRevision,
            CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            if (TransientFailuresRemaining-- > 0)
            {
                throw new IOException("busy");
            }

            if (ThrowConcurrency)
            {
                throw new PersistenceConcurrencyException(
                    "stale",
                    new InvalidOperationException());
            }

            return Task.FromResult(new VersionedDraft(draft, expectedRevision is null ? 0 : expectedRevision.Value + 1));
        }

        public Task<VersionedDraft?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<VersionedDraft?>(new VersionedDraft(current, 4));

        public Task DeleteAsync(Guid id, int expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoTransientErrors : ITransientPersistenceErrorPolicy
    {
        public bool IsTransient(Exception exception) => false;
    }

    private sealed class IoIsTransient : ITransientPersistenceErrorPolicy
    {
        public bool IsTransient(Exception exception) => exception is IOException;
    }
}

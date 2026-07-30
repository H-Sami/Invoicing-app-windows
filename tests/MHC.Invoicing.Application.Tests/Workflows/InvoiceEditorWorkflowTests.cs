using MHC.Invoicing.Application.Customers;
using MHC.Invoicing.Application.Drafts;
using MHC.Invoicing.Application.Items;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Application.Workflows;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Application.Tests.Workflows;

public sealed class InvoiceEditorWorkflowTests
{
    [Fact]
    public async Task InitializeUsesSaudiBusinessDateAndRequiresPaymentMethodSelection()
    {
        FakeDraftRepository repository = new();
        FakeCompanyProfile profile = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(
            repository,
            profile: profile,
            now: new DateTimeOffset(2026, 7, 23, 22, 30, 0, TimeSpan.Zero));

        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new DateOnly(2026, 7, 24), workflow.State.Draft.BusinessDate);
        Assert.Equal((PaymentMethod)0, workflow.State.Draft.PaymentMethod);
        Assert.False(workflow.State.CanIssue);
        Assert.True(workflow.State.IsCompanyProfileReady);
    }

    [Fact]
    public async Task SelectingPaymentMethodPersistsItAndClearsPaymentValidation()
    {
        FakeDraftRepository repository = new();
        FakeLookup lookup = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(repository, lookup: lookup);
        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await workflow.AddCatalogItemAsync(
            lookup.Item.Id,
            TestContext.Current.CancellationToken);

        Assert.Contains(
            workflow.State.Errors,
            error => error.Field == "paymentMethod" && error.Code == "invalid");
        Assert.False(workflow.State.CanIssue);

        await workflow.SetPaymentMethodAsync(
            PaymentMethod.BankTransfer,
            TestContext.Current.CancellationToken);

        Assert.Equal(PaymentMethod.BankTransfer, workflow.State.Draft.PaymentMethod);
        Assert.DoesNotContain(workflow.State.Errors, error => error.Field == "paymentMethod");
        Assert.True(workflow.State.CanIssue);
        Assert.Equal(2, workflow.State.Revision);
    }

    [Fact]
    public async Task MissingCompanyProfileBlocksIssuanceBeforeConfirmation()
    {
        FakeDraftRepository repository = new();
        FakeLookup lookup = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(
            repository,
            lookup,
            profile: new FakeCompanyProfile { IsReady = false });
        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await workflow.AddCatalogItemAsync(lookup.Item.Id, TestContext.Current.CancellationToken);

        Assert.False(workflow.State.IsCompanyProfileReady);
        Assert.False(workflow.State.CanIssue);
        await Assert.ThrowsAsync<CompanyProfileNotReadyException>(() =>
            workflow.IssueAsync(true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitializePersistsANewUnnumberedDraft()
    {
        FakeDraftRepository repository = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(repository);

        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(Guid.Empty, workflow.State.Draft.Id);
        Assert.Equal(0, workflow.State.Revision);
        Assert.Equal("عميل نقدي", workflow.State.Draft.Customer.Name);
        Assert.Empty(workflow.State.Draft.Lines);
        Assert.Single(repository.Saves);
    }

    [Fact]
    public async Task AddingCatalogLineAutosavesAndCalculatesVatTotals()
    {
        FakeDraftRepository repository = new();
        FakeLookup lookup = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(repository, lookup);
        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);

        await workflow.AddCatalogItemAsync(lookup.Item.Id, TestContext.Current.CancellationToken);
        await workflow.SetPaymentMethodAsync(PaymentMethod.Card, TestContext.Current.CancellationToken);

        Assert.Single(workflow.State.Draft.Lines);
        Assert.Equal(Money.FromRiyals(100m), workflow.State.Subtotal);
        Assert.Equal(Money.FromRiyals(15m), workflow.State.Vat);
        Assert.Equal(Money.FromRiyals(115m), workflow.State.GrandTotal);
        Assert.Equal(2, workflow.State.Revision);
        Assert.True(workflow.State.CanIssue);
        Assert.Equal(3, repository.Saves.Count);
    }

    [Fact]
    public async Task AddingOneOffLineCreatesAnIndependentDraftLine()
    {
        FakeDraftRepository repository = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(repository);
        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);

        await workflow.AddOneOffLineAsync(
            "استشارة خاصة",
            "JOB-1",
            "ساعة",
            2m,
            Money.FromRiyals(250m),
            VatCategory.Standard15,
            cancellationToken: TestContext.Current.CancellationToken);

        InvoiceDraftLine line = Assert.Single(workflow.State.Draft.Lines);
        Assert.Null(line.CatalogItemId);
        Assert.Equal("استشارة خاصة", line.Description);
        Assert.Equal("JOB-1", line.Sku);
        Assert.Equal("ساعة", line.Unit);
        Assert.Equal(Money.FromRiyals(575m), workflow.State.GrandTotal);
        Assert.Equal(1, workflow.State.Revision);
    }

    [Fact]
    public async Task InvalidQuantityIsDisplayedAndIsNotPersisted()
    {
        FakeDraftRepository repository = new();
        FakeLookup lookup = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(repository, lookup);
        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await workflow.AddCatalogItemAsync(lookup.Item.Id, TestContext.Current.CancellationToken);
        int saveCount = repository.Saves.Count;

        await workflow.UpdateLineAsync(
            workflow.State.Draft.Lines[0].Id,
            0m,
            Money.FromRiyals(100m),
            VatCategory.Standard15,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(workflow.State.Errors, error => error.Field.EndsWith("quantity", StringComparison.Ordinal));
        Assert.False(workflow.State.CanIssue);
        Assert.Equal(saveCount, repository.Saves.Count);
    }

    [Fact]
    public async Task CustomerSearchAndSelectionSnapshotCustomerIntoDraft()
    {
        FakeDraftRepository repository = new();
        FakeLookup lookup = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(repository, lookup);
        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<CustomerSuggestion> matches = await workflow.SearchCustomersAsync(
            "شركة", TestContext.Current.CancellationToken);
        await workflow.SelectCustomerAsync(matches[0], TestContext.Current.CancellationToken);

        Assert.Equal(lookup.Customer.Id, workflow.State.Draft.CustomerId);
        Assert.Equal("شركة الاختبار", workflow.State.Draft.Customer.Name);
        Assert.Equal(1, workflow.State.Revision);
    }

    [Fact]
    public async Task EditingCustomerSnapshotPreservesMasterLinkAndDoesNotWriteCustomerMaster()
    {
        FakeDraftRepository repository = new();
        FakeLookup lookup = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(repository, lookup);
        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await workflow.SelectCustomerAsync(lookup.Customer, TestContext.Current.CancellationToken);

        DraftParty edited = new(
            "اسم الفاتورة فقط",
            "Invoice-only name",
            "310111111111113",
            "1010999999",
            "عنوان مخصص للفاتورة");
        await workflow.SetCustomerSnapshotAsync(edited, TestContext.Current.CancellationToken);

        Assert.Equal(lookup.Customer.Id, workflow.State.Draft.CustomerId);
        Assert.Equal(edited, workflow.State.Draft.Customer);
        Assert.Equal("شركة الاختبار", lookup.Customer.NameArabic);
        Assert.Equal(2, workflow.State.Revision);
    }

    [Fact]
    public async Task IssuanceRequiresConfirmationAndUsesOnlyPersistedDraftIdentityAndRevision()
    {
        FakeDraftRepository repository = new();
        FakeLookup lookup = new();
        FakeIssuance issuance = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(repository, lookup, issuance);
        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await workflow.AddCatalogItemAsync(lookup.Item.Id, TestContext.Current.CancellationToken);
        await workflow.SetPaymentMethodAsync(PaymentMethod.BankTransfer, TestContext.Current.CancellationToken);

        Assert.Null(await workflow.IssueAsync(false, TestContext.Current.CancellationToken));
        IssuedInvoiceReference? issued = await workflow.IssueAsync(true, TestContext.Current.CancellationToken);

        Assert.NotNull(issued);
        Assert.Equal(workflow.State.Draft.Id, issuance.DraftId);
        Assert.Equal(2, issuance.Revision);
        Assert.Equal("MHC-2026-100", workflow.State.IssuedInvoice!.PublicNumber);
    }

    [Fact]
    public async Task CreditNoteIssuanceForwardsPersistedDocumentType()
    {
        FakeDraftRepository repository = new();
        FakeIssuance issuance = new();
        DateTimeOffset now = new(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
        DraftRecord creditNote = new(
            Guid.CreateVersion7(),
            InvoiceDocumentType.CreditNote,
            Guid.CreateVersion7(),
            null,
            new DateOnly(2026, 7, 23),
            new DraftParty("عميل", null, null, null, null),
            PaymentMethod.Cash,
            null,
            null,
            false,
            [new InvoiceDraftLine(
                Guid.CreateVersion7(), null, "مرتجع", null, "وحدة", 1m, Money.FromRiyals(100m),
                VatCategory.Standard15, null, null, Guid.CreateVersion7())],
            now,
            now);
        repository.Saves.Add((creditNote, null));
        InvoiceEditorWorkflow workflow = CreateWorkflow(repository, issuance: issuance);
        await workflow.InitializeAsync(creditNote.Id, TestContext.Current.CancellationToken);

        await workflow.IssueAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(InvoiceDocumentType.CreditNote, issuance.DocumentType);
    }

    [Fact]
    public async Task DocumentActionsAreUnavailableBeforeIssueAndRouteAfterIssue()
    {
        FakeDraftRepository repository = new();
        FakeLookup lookup = new();
        FakeDocuments documents = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(repository, lookup, documents: documents);
        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.PreviewAsync(TestContext.Current.CancellationToken));
        await workflow.AddCatalogItemAsync(lookup.Item.Id, TestContext.Current.CancellationToken);
        await workflow.SetPaymentMethodAsync(PaymentMethod.Card, TestContext.Current.CancellationToken);
        await workflow.IssueAsync(true, TestContext.Current.CancellationToken);
        await workflow.PreviewAsync(TestContext.Current.CancellationToken);
        await workflow.PrintAsync(TestContext.Current.CancellationToken);
        await workflow.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["preview", "print", "export"], documents.Actions);
    }

    [Fact]
    public async Task AutosaveConflictIsDisplayedAndBlocksIssuance()
    {
        FakeDraftRepository repository = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(repository);
        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        repository.ThrowConcurrency = true;

        await workflow.SelectCustomerAsync(
            new FakeLookup().Customer,
            TestContext.Current.CancellationToken);

        Assert.Equal(InvoiceEditorSaveStatus.Conflict, workflow.State.SaveStatus);
        Assert.False(workflow.State.CanIssue);
    }

    [Fact]
    public async Task ConcurrentMutationsWaitForThePreviousSaveAndUseItsRevisionAndState()
    {
        BlockingDraftRepository repository = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(repository);
        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        DateOnly changedDate = new(2026, 8, 1);

        Task firstMutation = workflow.SelectCustomerAsync(
            new FakeLookup().Customer,
            TestContext.Current.CancellationToken);
        await repository.FirstMutationEntered;

        Task secondMutation = workflow.SetBusinessDateAsync(
            changedDate,
            TestContext.Current.CancellationToken);

        Assert.False(repository.SecondMutationEntered.IsCompleted);
        repository.ReleaseFirstMutation();
        await Task.WhenAll(firstMutation, secondMutation);

        Assert.Equal([0, 1], repository.MutationExpectedRevisions);
        Assert.Equal(changedDate, workflow.State.Draft.BusinessDate);
        Assert.Equal("شركة الاختبار", workflow.State.Draft.Customer.Name);
        Assert.Equal(2, workflow.State.Revision);
        Assert.Equal(1, repository.MaximumConcurrentSaves);
    }

    [Fact]
    public async Task MutationWaitsUntilIssuanceCompletes()
    {
        BlockingDraftRepository repository = new();
        BlockingIssuance issuance = new();
        FakeLookup lookup = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(repository, lookup, issuance);
        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        repository.ReleaseFirstMutation();
        await workflow.AddCatalogItemAsync(lookup.Item.Id, TestContext.Current.CancellationToken);
        await workflow.SetPaymentMethodAsync(PaymentMethod.Card, TestContext.Current.CancellationToken);
        repository.BlockNextMutation();

        Task<IssuedInvoiceReference?> issue = workflow.IssueAsync(
            true,
            TestContext.Current.CancellationToken);
        await issuance.FirstCallEntered;
        Task mutation = workflow.SetBusinessDateAsync(
            new DateOnly(2026, 8, 2),
            TestContext.Current.CancellationToken);

        Assert.False(repository.BlockedMutationEntered.IsCompleted);
        issuance.ReleaseFirstCall();
        await issue;
        await repository.BlockedMutationEntered;
        repository.ReleaseBlockedMutation();
        await mutation;
    }

    [Fact]
    public async Task ConcurrentIssuanceCallsDoNotOverlap()
    {
        FakeDraftRepository repository = new();
        BlockingIssuance issuance = new();
        FakeLookup lookup = new();
        InvoiceEditorWorkflow workflow = CreateWorkflow(repository, lookup, issuance);
        await workflow.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await workflow.AddCatalogItemAsync(lookup.Item.Id, TestContext.Current.CancellationToken);
        await workflow.SetPaymentMethodAsync(PaymentMethod.Card, TestContext.Current.CancellationToken);

        Task<IssuedInvoiceReference?> first = workflow.IssueAsync(true, TestContext.Current.CancellationToken);
        await issuance.FirstCallEntered;
        Task<IssuedInvoiceReference?> second = workflow.IssueAsync(true, TestContext.Current.CancellationToken);

        Assert.False(issuance.SecondCallEntered.IsCompleted);
        issuance.ReleaseFirstCall();
        await first;
        await issuance.SecondCallEntered;
        issuance.ReleaseSecondCall();
        await second;
        Assert.Equal(1, issuance.MaximumConcurrentCalls);
    }

    private static InvoiceEditorWorkflow CreateWorkflow(
        IDraftRepository repository,
        FakeLookup? lookup = null,
        IInvoiceEditorIssuance? issuance = null,
        FakeDocuments? documents = null,
        FakeCompanyProfile? profile = null,
        DateTimeOffset? now = null) =>
        new(
            repository,
            new DraftAutosaveService(
                repository,
                new NoTransientErrors(),
                debounceDelay: TimeSpan.Zero),
            lookup ?? new FakeLookup(),
            issuance ?? new FakeIssuance(),
            documents ?? new FakeDocuments(),
            profile ?? new FakeCompanyProfile(),
            new FixedTimeProvider(now ?? new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero)));

    private sealed class FakeCompanyProfile : IInvoiceEditorCompanyProfile
    {
        public bool IsReady { get; set; } = true;

        public Task<InvoiceEditorCompanyProfile> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new InvoiceEditorCompanyProfile(IsReady));
    }

    private sealed class FakeLookup : IInvoiceEditorLookup
    {
        public CustomerSuggestion Customer { get; } = new(
            Guid.CreateVersion7(), "شركة الاختبار", "Test Co", "310000000000003", "1010101010",
            "الرياض", null, null);

        public CatalogItemSuggestion Item { get; } = new(
            Guid.CreateVersion7(), "خدمة", "Service", "S-1", "وحدة", Money.FromRiyals(100m),
            VatCategory.Standard15, false);

        public Task<IReadOnlyList<CustomerSuggestion>> SearchCustomersAsync(
            string? searchText,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CustomerSuggestion>>([Customer]);

        public Task<IReadOnlyList<CatalogItemSuggestion>> SearchCatalogAsync(
            string? searchText,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CatalogItemSuggestion>>([Item]);

        public Task<InvoiceDraftLine> SelectCatalogItemAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new InvoiceDraftLine(
                Guid.CreateVersion7(), Item.Id, Item.NameArabic, Item.Sku, Item.Unit, 1m,
                Item.DefaultUnitPrice, Item.VatCategory, null, null));
    }

    private sealed class FakeDraftRepository : IDraftRepository
    {
        public List<(DraftRecord Draft, int? Revision)> Saves { get; } = [];

        public bool ThrowConcurrency { get; set; }

        public Task<VersionedDraft> SaveAsync(
            DraftRecord draft,
            int? expectedRevision,
            CancellationToken cancellationToken = default)
        {
            if (ThrowConcurrency)
            {
                throw new PersistenceConcurrencyException("stale", new InvalidOperationException());
            }

            Saves.Add((draft, expectedRevision));
            return Task.FromResult(new VersionedDraft(draft, expectedRevision is null ? 0 : expectedRevision.Value + 1));
        }

        public Task<VersionedDraft?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<VersionedDraft?>(Saves.Count == 0
                ? null
                : new VersionedDraft(Saves[^1].Draft, Saves.Count - 1));

        public Task DeleteAsync(Guid id, int expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoTransientErrors : ITransientPersistenceErrorPolicy
    {
        public bool IsTransient(Exception exception) => false;
    }

    private sealed class FakeIssuance : IInvoiceEditorIssuance
    {
        public Guid DraftId { get; private set; }

        public int Revision { get; private set; } = -1;

        public InvoiceDocumentType DocumentType { get; private set; } = InvoiceDocumentType.TaxInvoice;

        public Task<IssuedInvoiceReference> IssueAsync(
            Guid draftId,
            int expectedRevision,
            InvoiceDocumentType documentType,
            CancellationToken cancellationToken = default)
        {
            DraftId = draftId;
            Revision = expectedRevision;
            DocumentType = documentType;
            return Task.FromResult(new IssuedInvoiceReference(
                Guid.CreateVersion7(), "MHC-2026-100", documentType));
        }
    }

    private sealed class BlockingDraftRepository : IDraftRepository
    {
        private readonly TaskCompletionSource _blockedMutationEntered = NewSignal();
        private readonly TaskCompletionSource _firstMutationEntered = NewSignal();
        private readonly TaskCompletionSource _releaseBlockedMutation = NewSignal();
        private readonly TaskCompletionSource _releaseFirstMutation = NewSignal();
        private readonly TaskCompletionSource _secondMutationEntered = NewSignal();
        private int _activeSaves;
        private bool _blockNextMutation;
        private int _mutationCalls;

        public Task BlockedMutationEntered => _blockedMutationEntered.Task;

        public Task FirstMutationEntered => _firstMutationEntered.Task;

        public int MaximumConcurrentSaves { get; private set; }

        public List<int> MutationExpectedRevisions { get; } = [];

        public Task SecondMutationEntered => _secondMutationEntered.Task;

        public void BlockNextMutation() => _blockNextMutation = true;

        public Task DeleteAsync(Guid id, int expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VersionedDraft?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<VersionedDraft?>(null);

        public void ReleaseBlockedMutation() => _releaseBlockedMutation.TrySetResult();

        public void ReleaseFirstMutation() => _releaseFirstMutation.TrySetResult();

        public async Task<VersionedDraft> SaveAsync(
            DraftRecord draft,
            int? expectedRevision,
            CancellationToken cancellationToken = default)
        {
            if (expectedRevision is null)
            {
                return new VersionedDraft(draft, 0);
            }

            int call = Interlocked.Increment(ref _mutationCalls);
            MutationExpectedRevisions.Add(expectedRevision.Value);
            int active = Interlocked.Increment(ref _activeSaves);
            MaximumConcurrentSaves = Math.Max(MaximumConcurrentSaves, active);
            try
            {
                if (_blockNextMutation)
                {
                    _blockNextMutation = false;
                    _blockedMutationEntered.TrySetResult();
                    await _releaseBlockedMutation.Task.WaitAsync(cancellationToken);
                }
                else if (call == 1)
                {
                    _firstMutationEntered.TrySetResult();
                    await _releaseFirstMutation.Task.WaitAsync(cancellationToken);
                }
                else if (call == 2)
                {
                    _secondMutationEntered.TrySetResult();
                }

                return new VersionedDraft(draft, expectedRevision.Value + 1);
            }
            finally
            {
                Interlocked.Decrement(ref _activeSaves);
            }
        }
    }

    private sealed class BlockingIssuance : IInvoiceEditorIssuance
    {
        private readonly TaskCompletionSource _firstCallEntered = NewSignal();
        private readonly TaskCompletionSource _releaseFirstCall = NewSignal();
        private readonly TaskCompletionSource _releaseSecondCall = NewSignal();
        private readonly TaskCompletionSource _secondCallEntered = NewSignal();
        private int _activeCalls;
        private int _calls;

        public Task FirstCallEntered => _firstCallEntered.Task;

        public int MaximumConcurrentCalls { get; private set; }

        public Task SecondCallEntered => _secondCallEntered.Task;

        public void ReleaseFirstCall() => _releaseFirstCall.TrySetResult();

        public void ReleaseSecondCall() => _releaseSecondCall.TrySetResult();

        public async Task<IssuedInvoiceReference> IssueAsync(
            Guid draftId,
            int expectedRevision,
            InvoiceDocumentType documentType,
            CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            int active = Interlocked.Increment(ref _activeCalls);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, active);
            try
            {
                if (call == 1)
                {
                    _firstCallEntered.TrySetResult();
                    await _releaseFirstCall.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    _secondCallEntered.TrySetResult();
                    await _releaseSecondCall.Task.WaitAsync(cancellationToken);
                }

                return new IssuedInvoiceReference(
                    Guid.CreateVersion7(), $"MHC-2026-{100 + call}", documentType);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }
    }

    private sealed class FakeDocuments : IInvoiceEditorDocuments
    {
        public List<string> Actions { get; } = [];

        public Task PreviewAsync(IssuedInvoiceReference invoice, CancellationToken cancellationToken = default) =>
            AddAsync("preview");

        public Task PrintAsync(IssuedInvoiceReference invoice, CancellationToken cancellationToken = default) =>
            AddAsync("print");

        public async Task<bool> ExportAsync(
            IssuedInvoiceReference invoice,
            CancellationToken cancellationToken = default)
        {
            await AddAsync("export");
            return true;
        }

        private Task AddAsync(string action)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

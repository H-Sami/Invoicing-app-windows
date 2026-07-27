using MHC.Invoicing.Application.Abstractions;
using MHC.Invoicing.Application.Drafts;
using MHC.Invoicing.Application.Invoices;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Application.Tests.Invoices;

public sealed class InvoiceHistoryUseCasesTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetInvoiceHistory_DelegatesFiltersToImmutableRepository()
    {
        FakeInvoiceRepository repository = new();
        GetInvoiceHistory useCase = new(repository);

        await useCase.ExecuteAsync("MHC", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), 25, TestContext.Current.CancellationToken);

        Assert.Equal(("MHC", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), 25), repository.SearchRequest);
    }

    [Fact]
    public async Task GetInvoiceDocument_ReturnsStoredDocument()
    {
        FakeInvoiceRepository repository = new() { Document = new InvoiceDocument([1, 2, 3], "application/pdf", Now) };
        GetInvoiceDocument useCase = new(repository);

        InvoiceDocument? result = await useCase.ExecuteAsync(Guid.Parse("019824f5-1ac0-7000-8000-000000000001"), TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], result!.PdfBytes);
    }

    [Fact]
    public async Task DuplicateInvoiceAsDraft_CopiesSnapshotButCreatesIndependentSaleLines()
    {
        Guid sourceLineId = Guid.Parse("019824f5-1ac0-7000-8000-000000000002");
        FakeInvoiceRepository invoices = new() { Snapshot = CreateSnapshot(sourceLineId) };
        FakeDraftRepository drafts = new();
        DuplicateInvoiceAsDraft useCase = new(invoices, drafts, new FakeClock());

        VersionedDraft duplicate = await useCase.ExecuteAsync(invoices.Snapshot.Id, TestContext.Current.CancellationToken);

        Assert.Equal(InvoiceDocumentType.TaxInvoice, duplicate.Draft.DocumentType);
        Assert.Null(duplicate.Draft.OriginalInvoiceId);
        Assert.Equal(invoices.Snapshot.SourceCustomerId, duplicate.Draft.CustomerId);
        Assert.Equal(new DateOnly(2026, 7, 23), duplicate.Draft.BusinessDate);
        Assert.Equal(invoices.Snapshot.Customer.NameArabic, duplicate.Draft.Customer.Name);
        Assert.Equal(invoices.Snapshot.Lines[0].Description, duplicate.Draft.Lines[0].Description);
        Assert.NotEqual(sourceLineId, duplicate.Draft.Lines[0].Id);
        Assert.Null(duplicate.Draft.Lines[0].OriginalInvoiceLineId);
        Assert.Same(drafts.Saved, duplicate);
    }

    [Fact]
    public async Task CreateCreditNoteAsDraftLinksEligibleSaleAndOriginalLines()
    {
        Guid sourceLineId = Guid.Parse("019824f5-1ac0-7000-8000-000000000002");
        FakeInvoiceRepository invoices = new()
        {
            Snapshot = CreateSnapshot(sourceLineId) with
            {
                DocumentType = InvoiceDocumentType.TaxInvoice,
                OriginalInvoiceId = null,
            },
        };
        FakeDraftRepository drafts = new();
        CreateCreditNoteAsDraft useCase = new(invoices, drafts, new FakeClock());

        VersionedDraft credit = await useCase.ExecuteAsync(
            invoices.Snapshot.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(InvoiceDocumentType.CreditNote, credit.Draft.DocumentType);
        Assert.Equal(invoices.Snapshot.Id, credit.Draft.OriginalInvoiceId);
        Assert.Equal(invoices.Snapshot.SourceCustomerId, credit.Draft.CustomerId);
        Assert.Equal(new DateOnly(2026, 7, 23), credit.Draft.BusinessDate);
        Assert.Equal(sourceLineId, credit.Draft.Lines[0].OriginalInvoiceLineId);
        Assert.NotEqual(sourceLineId, credit.Draft.Lines[0].Id);
    }

    [Theory]
    [InlineData(InvoiceDocumentType.CreditNote, false)]
    [InlineData(InvoiceDocumentType.TaxInvoice, true)]
    public async Task CreateCreditNoteAsDraftRejectsIneligibleInvoices(
        InvoiceDocumentType documentType,
        bool isVoided)
    {
        FakeInvoiceRepository invoices = new()
        {
            Snapshot = CreateSnapshot(Guid.NewGuid()) with
            {
                DocumentType = documentType,
                Void = isVoided ? new InvoiceVoidInfo("void", Now, "operator") : null,
            },
        };
        CreateCreditNoteAsDraft useCase = new(invoices, new FakeDraftRepository(), new FakeClock());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.ExecuteAsync(invoices.Snapshot.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VoidInvoice_UsesUtcClockAndPersistsFirstVoid()
    {
        FakeInvoiceRepository repository = new();
        VoidInvoice useCase = new(repository, new FakeClock());
        Guid invoiceId = Guid.Parse("019824f5-1ac0-7000-8000-000000000001");

        InvoiceVoidInfo result = await useCase.ExecuteAsync(invoiceId, "Incorrect customer", "Operator", TestContext.Current.CancellationToken);

        Assert.Equal(new InvoiceVoidInfo("Incorrect customer", Now, "Operator"), result);
        Assert.Equal((invoiceId, "Incorrect customer", "Operator", Now), repository.VoidRequest);
    }

    private static InvoiceSnapshot CreateSnapshot(Guid lineId) => new(
        Guid.Parse("019824f5-1ac0-7000-8000-000000000001"), 2026, 100, "MHC-2026-100",
        InvoiceDocumentType.CreditNote, Guid.NewGuid(), "MHC-2026-099", Guid.NewGuid(), new DateOnly(2026, 7, 20), Now, Now.ToOffset(TimeSpan.FromHours(3)),
        new PartySnapshot("Seller", null, "310123456789003", null, "Riyadh"), "Main", null, null,
        "Issuer", new PartySnapshot("Customer", "Customer EN", null, null, "Jeddah"), PaymentMethod.Card,
        "Title", "Notes", true, "SAR", new Money(1_000), new Money(150), new Money(1_150),
        [new InvoiceLineSnapshot(lineId, Guid.NewGuid(), null, "Service", "S1", "unit", 2m, new Money(500), VatCategory.Standard15, null, null, new Money(1_000), new Money(150), new Money(1_150))],
        null);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
        public DateTimeOffset SaudiNow => Now.ToOffset(TimeSpan.FromHours(3));
    }

    private sealed class FakeDraftRepository : IDraftRepository
    {
        public VersionedDraft Saved { get; private set; } = null!;
        public Task<VersionedDraft> SaveAsync(DraftRecord draft, int? expectedRevision, CancellationToken cancellationToken = default)
        {
            Saved = new VersionedDraft(draft, 0);
            return Task.FromResult(Saved);
        }
        public Task<VersionedDraft?> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id, int expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeInvoiceRepository : IInvoiceRepository
    {
        public InvoiceSnapshot Snapshot { get; set; } = null!;
        public InvoiceDocument? Document { get; set; }
        public (string?, DateOnly?, DateOnly?, int) SearchRequest { get; private set; }
        public (Guid, string, string, DateTimeOffset) VoidRequest { get; private set; }
        public Task<InvoiceSummary?> GetSummaryAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InvoiceSummary>> SearchAsync(string? searchText, DateOnly? fromBusinessDate, DateOnly? toBusinessDate, int limit, CancellationToken cancellationToken = default)
        {
            SearchRequest = (searchText, fromBusinessDate, toBusinessDate, limit);
            return Task.FromResult<IReadOnlyList<InvoiceSummary>>([]);
        }
        public Task<InvoiceSnapshot?> GetSnapshotAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<InvoiceSnapshot?>(Snapshot);
        public Task<InvoiceDocument?> GetDocumentAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Document);
        public Task<InvoiceVoidInfo> VoidAsync(Guid id, string reason, string operatorName, DateTimeOffset voidedAtUtc, CancellationToken cancellationToken = default)
        {
            VoidRequest = (id, reason, operatorName, voidedAtUtc);
            return Task.FromResult(new InvoiceVoidInfo(reason, voidedAtUtc, operatorName));
        }
    }
}

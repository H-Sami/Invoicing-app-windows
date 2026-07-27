using MHC.Invoicing.App.Workflows;
using MHC.Invoicing.Application.Drafts;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;

namespace MHC.Invoicing.Ui.Tests;

public sealed class InvoiceHistoryWorkflowTests
{
    [Fact]
    public async Task CreateCreditNoteDraftRoutesReturnedDraftIdentity()
    {
        FakeDataSource source = new();
        InvoiceHistoryWorkflow workflow = new(source, new FakePdfActions());

        VersionedDraft result = await workflow.CreateCreditNoteDraftAsync(
            source.Draft.Draft.OriginalInvoiceId!.Value,
            TestContext.Current.CancellationToken);

        Assert.Same(source.Draft, result);
        Assert.True(source.CreateCreditCalled);
    }

    private sealed class FakeDataSource : IInvoiceHistoryDataSource
    {
        public VersionedDraft Draft { get; } = new(new DraftRecord(
            Guid.NewGuid(), MHC.Invoicing.Domain.Invoices.InvoiceDocumentType.CreditNote, Guid.NewGuid(), null,
            new DateOnly(2026, 7, 23), new DraftParty("عميل", null, null, null, null),
            MHC.Invoicing.Domain.Invoices.PaymentMethod.Cash, null, null, false, [],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), 0);
        public bool CreateCreditCalled { get; private set; }
        public Task<VersionedDraft> CreateCreditNoteDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default)
        {
            CreateCreditCalled = true;
            return Task.FromResult(Draft);
        }
        public Task<IReadOnlyList<InvoiceSummary>> SearchAsync(string? searchText, DateOnly? fromBusinessDate, DateOnly? toBusinessDate, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InvoiceSummary?> GetSummaryAsync(Guid invoiceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InvoiceSnapshot?> GetSnapshotAsync(Guid invoiceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InvoiceDocument?> GetDocumentAsync(Guid invoiceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VersionedDraft> DuplicateAsDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InvoiceVoidInfo> VoidAsync(Guid invoiceId, string reason, string operatorName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakePdfActions : ICanonicalInvoicePdfActions
    {
        public Task PreviewAsync(byte[] canonicalPdfBytes, string publicNumber,
            InvoiceDocumentType documentType, InvoiceSnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task PrintAsync(byte[] canonicalPdfBytes, string publicNumber,
            InvoiceDocumentType documentType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ExportAsync(byte[] canonicalPdfBytes, string publicNumber,
            InvoiceDocumentType documentType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

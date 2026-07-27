using MHC.Invoicing.App.Workflows;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Application.Workflows;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Ui.Tests;

public sealed class InvoiceEditorDocumentAdapterTests
{
    [Fact]
    public async Task AllActionsForwardExactCanonicalBytesAndPublicNumber()
    {
        Guid invoiceId = Guid.CreateVersion7();
        byte[] canonicalPdf = "%PDF-1.7 canonical bytes"u8.ToArray();
        FakeInvoiceRepository repository = new(invoiceId, new InvoiceDocument(
            canonicalPdf,
            "application/pdf",
            DateTimeOffset.UtcNow));
        RecordingPdfActions actions = new();
        InvoiceEditorDocumentAdapter adapter = new(repository, actions);
        IssuedInvoiceReference reference = new(
            invoiceId,
            "MHC-2026-321",
            MHC.Invoicing.Domain.Invoices.InvoiceDocumentType.CreditNote);

        await adapter.PreviewAsync(reference, TestContext.Current.CancellationToken);
        await adapter.PrintAsync(reference, TestContext.Current.CancellationToken);
        bool exported = await adapter.ExportAsync(reference, TestContext.Current.CancellationToken);

        Assert.Equal(["preview", "print", "export"], actions.Names);
        Assert.All(actions.Bytes, bytes => Assert.Equal(canonicalPdf, bytes));
        Assert.All(actions.PublicNumbers, value => Assert.Equal(reference.PublicNumber, value));
        Assert.All(actions.DocumentTypes, value => Assert.Equal(reference.DocumentType, value));
        Assert.True(exported);
    }

    [Fact]
    public async Task ExportAsync_PropagatesPickerCancellation()
    {
        Guid invoiceId = Guid.CreateVersion7();
        FakeInvoiceRepository repository = new(invoiceId, new InvoiceDocument(
            "%PDF"u8.ToArray(),
            "application/pdf",
            DateTimeOffset.UtcNow));
        InvoiceEditorDocumentAdapter adapter = new(repository, new RecordingPdfActions(exportResult: false));
        IssuedInvoiceReference reference = new(
            invoiceId,
            "MHC-2026-322",
            MHC.Invoicing.Domain.Invoices.InvoiceDocumentType.TaxInvoice);

        bool exported = await adapter.ExportAsync(reference, TestContext.Current.CancellationToken);

        Assert.False(exported);
    }

    [Fact]
    public async Task PreviewAsync_PreservesSynchronizationContextAcrossAsynchronousRepositoryCalls()
    {
        Guid invoiceId = Guid.CreateVersion7();
        FakeInvoiceRepository repository = new(
            invoiceId,
            new InvoiceDocument("%PDF"u8.ToArray(), "application/pdf", DateTimeOffset.UtcNow),
            forceAsynchronousCompletion: true);
        RecordingPdfActions actions = new();
        InlineSynchronizationContext expected = new();
        SynchronizationContext? original = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(expected);
            await new InvoiceEditorDocumentAdapter(repository, actions).PreviewAsync(
                new IssuedInvoiceReference(invoiceId, "MHC-2026-323", InvoiceDocumentType.TaxInvoice),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        Assert.Same(expected, actions.PreviewSynchronizationContext);
    }

    private sealed class RecordingPdfActions(bool exportResult = true) : ICanonicalInvoicePdfActions
    {
        public List<string> Names { get; } = [];
        public List<byte[]> Bytes { get; } = [];
        public List<string> PublicNumbers { get; } = [];
        public List<MHC.Invoicing.Domain.Invoices.InvoiceDocumentType> DocumentTypes { get; } = [];
        public SynchronizationContext? PreviewSynchronizationContext { get; private set; }

        public Task PreviewAsync(byte[] canonicalPdfBytes, string publicNumber,
            MHC.Invoicing.Domain.Invoices.InvoiceDocumentType documentType,
            InvoiceSnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            RecordPreviewAsync(canonicalPdfBytes, publicNumber, documentType);

        private Task RecordPreviewAsync(byte[] bytes, string publicNumber, InvoiceDocumentType documentType)
        {
            PreviewSynchronizationContext = SynchronizationContext.Current;
            return RecordAsync("preview", bytes, publicNumber, documentType);
        }

        public Task PrintAsync(byte[] canonicalPdfBytes, string publicNumber,
            MHC.Invoicing.Domain.Invoices.InvoiceDocumentType documentType,
            CancellationToken cancellationToken = default) =>
            RecordAsync("print", canonicalPdfBytes, publicNumber, documentType);

        public Task<bool> ExportAsync(byte[] canonicalPdfBytes, string publicNumber,
            MHC.Invoicing.Domain.Invoices.InvoiceDocumentType documentType,
            CancellationToken cancellationToken = default)
        {
            Record("export", canonicalPdfBytes, publicNumber, documentType);
            return Task.FromResult(exportResult);
        }

        private Task RecordAsync(string name, byte[] bytes, string publicNumber,
            MHC.Invoicing.Domain.Invoices.InvoiceDocumentType documentType)
        {
            Record(name, bytes, publicNumber, documentType);
            return Task.CompletedTask;
        }

        private void Record(string name, byte[] bytes, string publicNumber,
            MHC.Invoicing.Domain.Invoices.InvoiceDocumentType documentType)
        {
            Names.Add(name);
            Bytes.Add(bytes.ToArray());
            PublicNumbers.Add(publicNumber);
            DocumentTypes.Add(documentType);
        }
    }

    private sealed class FakeInvoiceRepository(
        Guid invoiceId,
        InvoiceDocument document,
        bool forceAsynchronousCompletion = false) : IInvoiceRepository
    {
        public async Task<InvoiceDocument?> GetDocumentAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (forceAsynchronousCompletion)
                await Task.Yield();
            return id == invoiceId ? document : null;
        }

        public Task<InvoiceSummary?> GetSummaryAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<InvoiceSnapshot?> GetSnapshotAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (forceAsynchronousCompletion)
                await Task.Yield();
            return id == invoiceId ? CreateSnapshot(id) : null;
        }

        private static InvoiceSnapshot CreateSnapshot(Guid id) => new(
            id,
            2026,
            321,
            "MHC-2026-321",
            InvoiceDocumentType.TaxInvoice,
            null,
            null,
            null,
            new DateOnly(2026, 7, 23),
            new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 23, 3, 0, 0, TimeSpan.FromHours(3)),
            PartySnapshot.Create("Seller", null, null, null, null),
            "Riyadh",
            null,
            null,
            "Operator",
            PartySnapshot.Create("Customer", null, null, null, null),
            PaymentMethod.Cash,
            null,
            null,
            false,
            "SAR",
            Money.Zero,
            Money.Zero,
            Money.Zero,
            [],
            null);

        public Task<IReadOnlyList<InvoiceSummary>> SearchAsync(
            string? searchText,
            DateOnly? fromBusinessDate,
            DateOnly? toBusinessDate,
            int limit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<InvoiceVoidInfo> VoidAsync(
            Guid id,
            string reason,
            string operatorName,
            DateTimeOffset voidedAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
            SynchronizationContext? original = Current;
            try
            {
                SetSynchronizationContext(this);
                callback(state);
            }
            finally
            {
                SetSynchronizationContext(original);
            }
        }
    }
}

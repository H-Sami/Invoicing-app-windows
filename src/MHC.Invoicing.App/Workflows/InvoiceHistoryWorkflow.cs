using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;

namespace MHC.Invoicing.App.Workflows;

public interface IInvoiceHistoryDataSource
{
    Task<IReadOnlyList<InvoiceSummary>> SearchAsync(
        string? searchText,
        DateOnly? fromBusinessDate,
        DateOnly? toBusinessDate,
        int limit,
        CancellationToken cancellationToken = default);

    Task<InvoiceSummary?> GetSummaryAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    Task<InvoiceSnapshot?> GetSnapshotAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    Task<InvoiceDocument?> GetDocumentAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    Task<VersionedDraft> DuplicateAsDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    Task<VersionedDraft> CreateCreditNoteDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    Task<InvoiceVoidInfo> VoidAsync(
        Guid invoiceId,
        string reason,
        string operatorName,
        CancellationToken cancellationToken = default);
}

public interface ICanonicalInvoicePdfActions
{
    Task PreviewAsync(
        byte[] canonicalPdfBytes,
        string publicNumber,
        InvoiceDocumentType documentType,
        InvoiceSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task PrintAsync(
        byte[] canonicalPdfBytes,
        string publicNumber,
        InvoiceDocumentType documentType,
        CancellationToken cancellationToken = default);

    Task<bool> ExportAsync(
        byte[] canonicalPdfBytes,
        string publicNumber,
        InvoiceDocumentType documentType,
        CancellationToken cancellationToken = default);
}

public sealed record FinalizedInvoiceHistoryItem(
    InvoiceSummary Summary,
    InvoiceSnapshot Snapshot,
    InvoiceDocument Document);

public sealed class InvoiceHistoryWorkflow(
    IInvoiceHistoryDataSource dataSource,
    ICanonicalInvoicePdfActions pdfActions)
{
    public Task<IReadOnlyList<InvoiceSummary>> SearchAsync(
        string? searchText,
        DateOnly? fromBusinessDate = null,
        DateOnly? toBusinessDate = null,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        dataSource.SearchAsync(searchText, fromBusinessDate, toBusinessDate, limit, cancellationToken);

    public async Task<FinalizedInvoiceHistoryItem> GetFinalizedAsync(
        Guid invoiceId,
        CancellationToken cancellationToken = default)
    {
        InvoiceSummary summary = await dataSource.GetSummaryAsync(invoiceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvoiceNotFoundException(invoiceId);
        InvoiceSnapshot snapshot = await dataSource.GetSnapshotAsync(invoiceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvoiceNotFoundException(invoiceId);
        InvoiceDocument document = await dataSource.GetDocumentAsync(invoiceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException($"Finalized invoice {invoiceId} has no canonical PDF.");
        return new FinalizedInvoiceHistoryItem(summary, snapshot, document);
    }

    public Task<VersionedDraft> DuplicateAsDraftAsync(
        Guid invoiceId,
        CancellationToken cancellationToken = default) =>
        dataSource.DuplicateAsDraftAsync(invoiceId, cancellationToken);

    public Task<VersionedDraft> CreateCreditNoteDraftAsync(
        Guid invoiceId,
        CancellationToken cancellationToken = default) =>
        dataSource.CreateCreditNoteDraftAsync(invoiceId, cancellationToken);

    public async Task<InvoiceVoidInfo> VoidAsync(
        Guid invoiceId,
        string reason,
        string operatorName,
        CancellationToken cancellationToken = default)
    {
        InvoiceSummary summary = await dataSource.GetSummaryAsync(invoiceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvoiceNotFoundException(invoiceId);
        if (summary.IsVoided)
        {
            throw new InvoiceAlreadyVoidedException(invoiceId);
        }

        return await dataSource.VoidAsync(invoiceId, reason, operatorName, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PreviewAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        FinalizedInvoiceHistoryItem item = await GetFinalizedAsync(invoiceId, cancellationToken);
        await pdfActions.PreviewAsync(
            item.Document.PdfBytes,
            item.Summary.PublicNumber,
            item.Summary.DocumentType,
            item.Snapshot,
            cancellationToken);
    }

    public async Task PrintAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        (InvoiceDocument document, InvoiceSummary summary) = await GetCanonicalDocumentAsync(
            invoiceId, cancellationToken);
        await pdfActions.PrintAsync(
            document.PdfBytes,
            summary.PublicNumber,
            summary.DocumentType,
            cancellationToken);
    }

    public async Task<bool> ExportAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        (InvoiceDocument document, InvoiceSummary summary) = await GetCanonicalDocumentAsync(
            invoiceId, cancellationToken);
        return await pdfActions.ExportAsync(
            document.PdfBytes,
            summary.PublicNumber,
            summary.DocumentType,
            cancellationToken);
    }

    private async Task<(InvoiceDocument Document, InvoiceSummary Summary)> GetCanonicalDocumentAsync(
        Guid invoiceId,
        CancellationToken cancellationToken)
    {
        InvoiceDocument document = await dataSource.GetDocumentAsync(invoiceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException($"Finalized invoice {invoiceId} has no canonical PDF.");
        InvoiceSummary summary = await dataSource.GetSummaryAsync(invoiceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvoiceNotFoundException(invoiceId);
        return (document, summary);
    }
}

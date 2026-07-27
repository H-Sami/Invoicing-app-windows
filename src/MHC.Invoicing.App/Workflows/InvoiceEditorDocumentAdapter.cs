using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Application.Workflows;

namespace MHC.Invoicing.App.Workflows;

public sealed class InvoiceEditorDocumentAdapter(
    IInvoiceRepository invoices,
    ICanonicalInvoicePdfActions actions) : IInvoiceEditorDocuments
{
    public async Task PreviewAsync(
        IssuedInvoiceReference invoice,
        CancellationToken cancellationToken = default)
    {
        InvoiceDocument document = await LoadAsync(invoice, cancellationToken);
        InvoiceSnapshot snapshot = await invoices.GetSnapshotAsync(invoice.Id, cancellationToken)
            ?? throw new InvalidOperationException("The issued immutable snapshot was not found.");
        await actions.PreviewAsync(
            document.PdfBytes, invoice.PublicNumber, invoice.DocumentType, snapshot, cancellationToken);
    }

    public async Task PrintAsync(
        IssuedInvoiceReference invoice,
        CancellationToken cancellationToken = default)
    {
        InvoiceDocument document = await LoadAsync(invoice, cancellationToken);
        await actions.PrintAsync(
            document.PdfBytes, invoice.PublicNumber, invoice.DocumentType, cancellationToken);
    }

    public async Task<bool> ExportAsync(
        IssuedInvoiceReference invoice,
        CancellationToken cancellationToken = default)
    {
        InvoiceDocument document = await LoadAsync(invoice, cancellationToken);
        return await actions.ExportAsync(
            document.PdfBytes, invoice.PublicNumber, invoice.DocumentType, cancellationToken);
    }

    private async Task<InvoiceDocument> LoadAsync(
        IssuedInvoiceReference invoice,
        CancellationToken cancellationToken) =>
        await invoices.GetDocumentAsync(invoice.Id, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The issued canonical PDF was not found.");
}

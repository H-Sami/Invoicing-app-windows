using MHC.Invoicing.Application.Abstractions;
using MHC.Invoicing.Application.Drafts;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;

namespace MHC.Invoicing.Application.Invoices;

public sealed class GetInvoiceHistory(IInvoiceRepository invoices)
{
    public Task<IReadOnlyList<InvoiceSummary>> ExecuteAsync(
        string? searchText,
        DateOnly? fromBusinessDate,
        DateOnly? toBusinessDate,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        invoices.SearchAsync(searchText, fromBusinessDate, toBusinessDate, limit, cancellationToken);
}

public sealed class GetInvoiceDocument(IInvoiceRepository invoices)
{
    public Task<InvoiceDocument?> ExecuteAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        invoices.GetDocumentAsync(invoiceId, cancellationToken);
}

public sealed class DuplicateInvoiceAsDraft(
    IInvoiceRepository invoices,
    IDraftRepository drafts,
    IClock clock)
{
    public async Task<VersionedDraft> ExecuteAsync(
        Guid invoiceId,
        CancellationToken cancellationToken = default)
    {
        InvoiceSnapshot source = await invoices.GetSnapshotAsync(invoiceId, cancellationToken)
            ?? throw new InvoiceNotFoundException(invoiceId);
        DateTimeOffset now = clock.UtcNow;
        DraftRecord duplicate = new(
            Guid.CreateVersion7(),
            InvoiceDocumentType.TaxInvoice,
            null,
            source.SourceCustomerId,
            DateOnly.FromDateTime(clock.SaudiNow.DateTime),
            new DraftParty(
                source.Customer.NameArabic,
                source.Customer.NameEnglish,
                source.Customer.VatNumber,
                source.Customer.CommercialRegistration,
                source.Customer.Address),
            source.PaymentMethod,
            source.Title,
            source.Notes,
            source.ShowNotes,
            source.Lines.Select(line => new InvoiceDraftLine(
                Guid.CreateVersion7(),
                line.SourceCatalogItemId,
                line.Description,
                line.Sku,
                line.Unit,
                line.Quantity,
                line.UnitPrice,
                line.VatCategory,
                line.TaxExemptionReasonCode,
                line.TaxExemptionReason)).ToArray(),
            now,
            now);
        return await drafts.SaveAsync(duplicate, expectedRevision: null, cancellationToken);
    }
}

public sealed class CreateCreditNoteAsDraft(
    IInvoiceRepository invoices,
    IDraftRepository drafts,
    IClock clock)
{
    public async Task<VersionedDraft> ExecuteAsync(
        Guid invoiceId,
        CancellationToken cancellationToken = default)
    {
        InvoiceSnapshot source = await invoices.GetSnapshotAsync(invoiceId, cancellationToken)
            ?? throw new InvoiceNotFoundException(invoiceId);
        if (source.DocumentType != InvoiceDocumentType.TaxInvoice || source.Void is not null)
        {
            throw new InvalidOperationException("Only a non-voided tax invoice can be credited.");
        }

        DateTimeOffset now = clock.UtcNow;
        DraftRecord credit = new(
            Guid.CreateVersion7(),
            InvoiceDocumentType.CreditNote,
            source.Id,
            source.SourceCustomerId,
            DateOnly.FromDateTime(clock.SaudiNow.DateTime),
            new DraftParty(
                source.Customer.NameArabic,
                source.Customer.NameEnglish,
                source.Customer.VatNumber,
                source.Customer.CommercialRegistration,
                source.Customer.Address),
            source.PaymentMethod,
            source.Title,
            source.Notes,
            source.ShowNotes,
            source.Lines.Select(line => new InvoiceDraftLine(
                Guid.CreateVersion7(),
                line.SourceCatalogItemId,
                line.Description,
                line.Sku,
                line.Unit,
                line.Quantity,
                line.UnitPrice,
                line.VatCategory,
                line.TaxExemptionReasonCode,
                line.TaxExemptionReason,
                line.Id)).ToArray(),
            now,
            now);
        return await drafts.SaveAsync(credit, expectedRevision: null, cancellationToken);
    }
}

public sealed class VoidInvoice(IInvoiceRepository invoices, IClock clock)
{
    public Task<InvoiceVoidInfo> ExecuteAsync(
        Guid invoiceId,
        string reason,
        string operatorName,
        CancellationToken cancellationToken = default)
    {
        string normalizedReason = Required(reason, 1_000, nameof(reason));
        string normalizedOperator = Required(operatorName, 200, nameof(operatorName));
        return invoices.VoidAsync(invoiceId, normalizedReason, normalizedOperator, clock.UtcNow, cancellationToken);
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        string normalized = value.Trim();
        if (normalized.Length == 0 || normalized.Length > maximumLength)
        {
            throw new ArgumentException($"Value must contain between 1 and {maximumLength} characters.", parameterName);
        }

        return normalized;
    }
}

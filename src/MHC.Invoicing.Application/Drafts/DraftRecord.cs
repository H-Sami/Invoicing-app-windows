using MHC.Invoicing.Domain.Invoices;

namespace MHC.Invoicing.Application.Drafts;

public sealed record DraftRecord(
    Guid Id,
    InvoiceDocumentType DocumentType,
    Guid? OriginalInvoiceId,
    Guid? CustomerId,
    DateOnly BusinessDate,
    DraftParty Customer,
    PaymentMethod PaymentMethod,
    string? Title,
    string? Notes,
    bool ShowNotes,
    IReadOnlyList<InvoiceDraftLine> Lines,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Application.Documents;

public sealed record InvoiceDocumentLine(
    Guid Id,
    string Description,
    string? Sku,
    string Unit,
    decimal Quantity,
    Money UnitPrice,
    VatCategory VatCategory,
    string? TaxExemptionReasonCode,
    string? TaxExemptionReason,
    Money Discount,
    Money Net,
    Money Vat,
    Money Gross);

public sealed record InvoiceDocumentModel(
    string PublicNumber,
    Guid Serial,
    InvoiceDocumentType DocumentType,
    string? OriginalPublicNumber,
    DateOnly BusinessDate,
    DateTimeOffset IssuedAtSaudi,
    PartySnapshot Seller,
    PartySnapshot Customer,
    string Branch,
    string OperatorName,
    string PaymentMethod,
    string? Title,
    string? Notes,
    bool ShowNotes,
    byte[]? SellerLogoBytes,
    string? SellerLogoMimeType,
    byte[] QrPngBytes,
    IReadOnlyList<InvoiceDocumentLine> Lines,
    Money Subtotal,
    Money Vat,
    Money GrandTotal);

public interface IInvoiceHtmlRenderer
{
    string Render(InvoiceDocumentModel model);
}

public interface IInvoicePdfRenderer
{
    Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default);
}

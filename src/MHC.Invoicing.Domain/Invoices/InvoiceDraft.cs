using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Domain.Invoices;

public enum PaymentMethod
{
    Cash = 1,
    Card = 2,
    BankTransfer = 3,
    Credit = 4,
    Other = 5,
}

public sealed record DraftParty(
    string Name,
    string? NameEnglish,
    string? VatNumber,
    string? CommercialRegistration,
    string? Address);

public sealed record InvoiceDraftLine(
    Guid Id,
    Guid? CatalogItemId,
    string Description,
    string? Sku,
    string Unit,
    decimal Quantity,
    Money UnitPrice,
    VatCategory VatCategory,
    string? TaxExemptionReasonCode,
    string? TaxExemptionReason,
    Guid? OriginalInvoiceLineId = null);

public sealed class InvoiceDraft
{
    private IReadOnlyList<InvoiceDraftLine> _lines = Array.Empty<InvoiceDraftLine>();

    private InvoiceDraft()
    {
        Seller = null!;
        Customer = null!;
    }

    private InvoiceDraft(Guid id)
        : this()
    {
        Id = id;
    }

    public Guid Id { get; private set; }

    public DateOnly BusinessDate { get; private set; }

    public InvoiceDocumentType DocumentType { get; private set; }

    public DraftParty Seller { get; private set; }

    public DraftParty Customer { get; private set; }

    public PaymentMethod PaymentMethod { get; private set; }

    public Guid? OriginalInvoiceId { get; private set; }

    public InvoiceNumber? InvoiceNumber { get; private set; }

    public DocumentSerial? DocumentSerial { get; private set; }

    public IReadOnlyList<InvoiceDraftLine> Lines => _lines;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static InvoiceDraft Create(
        DateOnly businessDate,
        InvoiceDocumentType documentType,
        DraftParty seller,
        DraftParty customer,
        PaymentMethod paymentMethod,
        Guid? originalInvoiceId,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(seller);
        ArgumentNullException.ThrowIfNull(customer);
        ValidateUtc(createdAtUtc, nameof(createdAtUtc));

        return new InvoiceDraft(Guid.CreateVersion7())
        {
            BusinessDate = businessDate,
            DocumentType = documentType,
            Seller = seller,
            Customer = customer,
            PaymentMethod = paymentMethod,
            OriginalInvoiceId = originalInvoiceId,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
        };
    }

    public void ReplaceLines(IEnumerable<InvoiceDraftLine> lines, DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ValidateMutationTime(updatedAtUtc);
        _lines = Array.AsReadOnly(lines.ToArray());
        UpdatedAtUtc = updatedAtUtc;
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be a non-default UTC value.", parameterName);
        }
    }

    private void ValidateMutationTime(DateTimeOffset value)
    {
        ValidateUtc(value, nameof(value));
        if (value < UpdatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Update timestamp cannot precede the current version.");
        }
    }
}

using MHC.Invoicing.Application.Drafts;
using MHC.Invoicing.Domain.Catalog;
using MHC.Invoicing.Domain.Customers;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Application.Persistence;

public sealed record VersionedCustomer(Customer Customer, int Revision);

public sealed record VersionedCatalogItem(CatalogItem CatalogItem, int Revision);

public sealed record CompanyProfileSettings(
    string NameArabic,
    string? NameEnglish,
    string VatNumber,
    string? CommercialRegistration,
    string Branch,
    string Address,
    string OperatorName,
    PaymentMethod DefaultPaymentMethod,
    byte[]? LogoBytes = null,
    string? LogoMimeType = null);

public sealed record VersionedCompanyProfile(CompanyProfileSettings Profile, int Revision);

public interface ICompanyProfileRepository
{
    Task<VersionedCompanyProfile?> GetAsync(CancellationToken cancellationToken = default);

    Task<VersionedCompanyProfile> SaveAsync(
        CompanyProfileSettings profile,
        int? expectedRevision,
        CancellationToken cancellationToken = default);
}

public interface ICustomerRepository
{
    Task<VersionedCustomer> AddAsync(Customer customer, CancellationToken cancellationToken = default);

    Task<VersionedCustomer?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VersionedCustomer>> SearchAsync(
        string? searchText,
        bool includeArchived,
        int limit,
        CancellationToken cancellationToken = default);

    Task<VersionedCustomer> UpdateAsync(
        Customer customer,
        int expectedRevision,
        CancellationToken cancellationToken = default);
}

public interface ICatalogItemRepository
{
    Task<VersionedCatalogItem> AddAsync(CatalogItem catalogItem, CancellationToken cancellationToken = default);

    Task<VersionedCatalogItem?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VersionedCatalogItem>> SearchAsync(
        string? searchText,
        bool includeArchived,
        int limit,
        CancellationToken cancellationToken = default);

    Task<VersionedCatalogItem> UpdateAsync(
        CatalogItem catalogItem,
        int expectedRevision,
        CancellationToken cancellationToken = default);
}

public sealed record InvoiceSummary(
    Guid Id,
    string PublicNumber,
    InvoiceDocumentType DocumentType,
    DateOnly BusinessDate,
    DateTimeOffset IssuedAtUtc,
    string CustomerNameArabic,
    string? CustomerNameEnglish,
    Money GrandTotal,
    bool IsVoided);

public sealed record InvoiceLineSnapshot(
    Guid Id,
    Guid? SourceCatalogItemId,
    Guid? OriginalInvoiceLineId,
    string Description,
    string? Sku,
    string Unit,
    decimal Quantity,
    Money UnitPrice,
    VatCategory VatCategory,
    string? TaxExemptionReasonCode,
    string? TaxExemptionReason,
    Money Net,
    Money Vat,
    Money Gross);

public sealed record InvoiceVoidInfo(string Reason, DateTimeOffset VoidedAtUtc, string OperatorName);

public sealed record InvoiceSnapshot(
    Guid Id,
    int IssuanceYear,
    int Sequence,
    string PublicNumber,
    InvoiceDocumentType DocumentType,
    Guid? OriginalInvoiceId,
    string? OriginalInvoicePublicNumber,
    Guid? SourceCustomerId,
    DateOnly BusinessDate,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset IssuedAtSaudi,
    PartySnapshot Seller,
    string SellerBranch,
    byte[]? SellerLogoBytes,
    string? SellerLogoMimeType,
    string OperatorName,
    PartySnapshot Customer,
    PaymentMethod PaymentMethod,
    string? Title,
    string? Notes,
    bool ShowNotes,
    string Currency,
    Money Subtotal,
    Money Vat,
    Money GrandTotal,
    IReadOnlyList<InvoiceLineSnapshot> Lines,
    InvoiceVoidInfo? Void);

public sealed class InvoiceDocument
{
    private readonly byte[] _pdfBytes;

    public InvoiceDocument(byte[] pdfBytes, string mimeType, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        _pdfBytes = pdfBytes.ToArray();
        MimeType = mimeType;
        CreatedAtUtc = createdAtUtc;
    }

    public byte[] PdfBytes => _pdfBytes.ToArray();

    public string MimeType { get; }

    public DateTimeOffset CreatedAtUtc { get; }
}

public interface IInvoiceRepository
{
    Task<InvoiceSummary?> GetSummaryAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InvoiceSummary>> SearchAsync(
        string? searchText,
        DateOnly? fromBusinessDate,
        DateOnly? toBusinessDate,
        int limit,
        CancellationToken cancellationToken = default);

    Task<InvoiceSnapshot?> GetSnapshotAsync(Guid id, CancellationToken cancellationToken = default);

    Task<InvoiceDocument?> GetDocumentAsync(Guid id, CancellationToken cancellationToken = default);

    Task<InvoiceVoidInfo> VoidAsync(
        Guid id,
        string reason,
        string operatorName,
        DateTimeOffset voidedAtUtc,
        CancellationToken cancellationToken = default);
}

public sealed class InvoiceNotFoundException(Guid invoiceId)
    : InvalidOperationException($"Invoice {invoiceId} does not exist.");

public sealed class InvoiceAlreadyVoidedException(Guid invoiceId)
    : InvalidOperationException($"Invoice {invoiceId} has already been voided.");

public sealed record VersionedDraft(DraftRecord Draft, int Revision);

public interface IDraftRepository
{
    Task<VersionedDraft> SaveAsync(
        DraftRecord draft,
        int? expectedRevision,
        CancellationToken cancellationToken = default);

    Task<VersionedDraft?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, int expectedRevision, CancellationToken cancellationToken = default);
}

public sealed class PersistenceConcurrencyException : Exception
{
    public PersistenceConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

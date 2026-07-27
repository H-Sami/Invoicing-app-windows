using MHC.Invoicing.Domain.Invoices;

namespace MHC.Invoicing.Infrastructure.Persistence.Entities;

public sealed class CompanyProfileEntity
{
    public int Id { get; set; }
    public int Revision { get; set; }
    public string NameArabic { get; set; } = string.Empty;
    public string? NameEnglish { get; set; }
    public string VatNumber { get; set; } = string.Empty;
    public string? CommercialRegistration { get; set; }
    public string Branch { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string OperatorName { get; set; } = string.Empty;
    public PaymentMethod DefaultPaymentMethod { get; set; }
    public byte[]? LogoBytes { get; set; }
    public string? LogoMimeType { get; set; }
    public long CreatedAtUtcMs { get; set; }
    public long UpdatedAtUtcMs { get; set; }
}

public sealed class CustomerEntity
{
    public Guid Id { get; set; }
    public string NameArabic { get; set; } = string.Empty;
    public string? NameEnglish { get; set; }
    public string SearchNameArabic { get; set; } = string.Empty;
    public string SearchNameEnglish { get; set; } = string.Empty;
    public string? VatNumber { get; set; }
    public string? CommercialRegistration { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool IsArchived { get; set; }
    public int Revision { get; set; }
    public long CreatedAtUtcMs { get; set; }
    public long UpdatedAtUtcMs { get; set; }
}

public sealed class CatalogItemEntity
{
    public Guid Id { get; set; }
    public string NameArabic { get; set; } = string.Empty;
    public string? NameEnglish { get; set; }
    public string SearchNameArabic { get; set; } = string.Empty;
    public string SearchNameEnglish { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string SearchSku { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public long DefaultUnitPriceHalalah { get; set; }
    public VatCategory VatCategory { get; set; }
    public bool IsArchived { get; set; }
    public int Revision { get; set; }
    public long CreatedAtUtcMs { get; set; }
    public long UpdatedAtUtcMs { get; set; }
}

public sealed class InvoiceDraftEntity
{
    public Guid Id { get; set; }
    public int Revision { get; set; }
    public InvoiceDocumentType DocumentType { get; set; }
    public Guid? OriginalInvoiceId { get; set; }
    public Guid? CustomerId { get; set; }
    public string BusinessDate { get; set; } = string.Empty;
    public string CustomerNameArabic { get; set; } = string.Empty;
    public string? CustomerNameEnglish { get; set; }
    public string? CustomerVatNumber { get; set; }
    public string? CustomerCommercialRegistration { get; set; }
    public string? CustomerAddress { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public string? Title { get; set; }
    public string? Notes { get; set; }
    public bool ShowNotes { get; set; }
    public long CreatedAtUtcMs { get; set; }
    public long UpdatedAtUtcMs { get; set; }
    public List<InvoiceDraftLineEntity> Lines { get; } = [];
}

public sealed class InvoiceDraftLineEntity
{
    public Guid Id { get; set; }
    public Guid DraftId { get; set; }
    public int Position { get; set; }
    public Guid? CatalogItemId { get; set; }
    public Guid? OriginalInvoiceLineId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string Unit { get; set; } = string.Empty;
    public long QuantityMilliunits { get; set; }
    public long UnitPriceHalalah { get; set; }
    public VatCategory VatCategory { get; set; }
    public string? TaxExemptionReasonCode { get; set; }
    public string? TaxExemptionReason { get; set; }
    public InvoiceDraftEntity Draft { get; set; } = null!;
}

public sealed class InvoiceSequenceEntity
{
    public int IssuanceYear { get; set; }
    public int NextValue { get; set; }
}

public sealed class InvoiceEntity
{
    public Guid Id { get; set; }
    public int IssuanceYear { get; set; }
    public int Sequence { get; set; }
    public string PublicNumber { get; set; } = string.Empty;
    public InvoiceDocumentType DocumentType { get; set; }
    public Guid? OriginalInvoiceId { get; set; }
    public Guid? SourceCustomerId { get; set; }
    public string BusinessDate { get; set; } = string.Empty;
    public long IssuedAtUtcMs { get; set; }
    public string IssuedAtSaudiLocal { get; set; } = string.Empty;
    public int IssuedSaudiOffsetMinutes { get; set; }
    public string SellerNameArabic { get; set; } = string.Empty;
    public string? SellerNameEnglish { get; set; }
    public string SellerVatNumber { get; set; } = string.Empty;
    public string? SellerCommercialRegistration { get; set; }
    public string SellerBranch { get; set; } = string.Empty;
    public string SellerAddress { get; set; } = string.Empty;
    public byte[]? SellerLogoBytes { get; set; }
    public string? SellerLogoMimeType { get; set; }
    public string OperatorName { get; set; } = string.Empty;
    public string CustomerNameArabic { get; set; } = string.Empty;
    public string? CustomerNameEnglish { get; set; }
    public string CustomerSearchName { get; set; } = string.Empty;
    public string? CustomerVatNumber { get; set; }
    public string? CustomerCommercialRegistration { get; set; }
    public string? CustomerAddress { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public string? Title { get; set; }
    public string? Notes { get; set; }
    public bool ShowNotes { get; set; }
    public string Currency { get; set; } = "SAR";
    public long SubtotalHalalah { get; set; }
    public long VatHalalah { get; set; }
    public long GrandTotalHalalah { get; set; }
    public List<InvoiceLineEntity> Lines { get; } = [];
    public InvoiceDocumentEntity? Document { get; set; }
    public InvoiceVoidEntity? Void { get; set; }
}

public sealed class InvoiceLineEntity
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public int Position { get; set; }
    public Guid? SourceCatalogItemId { get; set; }
    public Guid? OriginalInvoiceLineId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string Unit { get; set; } = string.Empty;
    public long QuantityMilliunits { get; set; }
    public long UnitPriceHalalah { get; set; }
    public VatCategory VatCategory { get; set; }
    public string? TaxExemptionReasonCode { get; set; }
    public string? TaxExemptionReason { get; set; }
    public long NetHalalah { get; set; }
    public long VatHalalah { get; set; }
    public long GrossHalalah { get; set; }
    public InvoiceEntity Invoice { get; set; } = null!;
}

public sealed class InvoiceDocumentEntity
{
    public Guid InvoiceId { get; set; }
    public byte[] PdfBytes { get; set; } = [];
    public byte[] Sha256 { get; set; } = [];
    public long ByteLength { get; set; }
    public string MimeType { get; set; } = "application/pdf";
    public long CreatedAtUtcMs { get; set; }
    public InvoiceEntity Invoice { get; set; } = null!;
}

public sealed class InvoiceVoidEntity
{
    public Guid InvoiceId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public long VoidedAtUtcMs { get; set; }
    public string OperatorName { get; set; } = string.Empty;
    public InvoiceEntity Invoice { get; set; } = null!;
}

public sealed class AuditEventEntity
{
    public Guid Id { get; set; }
    public Guid? InvoiceId { get; set; }
    public int EventType { get; set; }
    public long OccurredAtUtcMs { get; set; }
    public string? OperatorName { get; set; }
    public string? DetailsJson { get; set; }
}

public sealed class AppSettingEntity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public long UpdatedAtUtcMs { get; set; }
}

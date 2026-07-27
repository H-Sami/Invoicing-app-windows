using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.Search;
using MHC.Invoicing.Domain.Validation;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Domain.Catalog;

public sealed class CatalogItem
{
    private CatalogItem()
    {
        NameArabic = null!;
        SearchNameArabic = null!;
    }

    private CatalogItem(Guid id)
        : this()
    {
        Id = id;
    }

    public Guid Id { get; private set; }

    public string NameArabic { get; private set; }

    public string? NameEnglish { get; private set; }

    public string SearchNameArabic { get; private set; }

    public string SearchNameEnglish { get; private set; } = string.Empty;

    public string? Sku { get; private set; }

    public string SearchSku { get; private set; } = string.Empty;

    public UnitOfMeasure Unit { get; private set; }

    public Money DefaultUnitPrice { get; private set; }

    public VatCategory VatCategory { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public bool IsArchived { get; private set; }

    public static CatalogItem Create(
        string nameArabic,
        string? nameEnglish,
        string? sku,
        UnitOfMeasure unit,
        Money defaultUnitPrice,
        VatCategory vatCategory,
        DateTimeOffset createdAtUtc)
    {
        ValidateUtc(createdAtUtc, nameof(createdAtUtc));
        CatalogItem item = new(Guid.CreateVersion7())
        {
            CreatedAtUtc = createdAtUtc,
        };
        item.Update(nameArabic, nameEnglish, sku, unit, defaultUnitPrice, vatCategory, createdAtUtc);
        return item;
    }

    internal static CatalogItem Rehydrate(
        Guid id,
        string nameArabic,
        string? nameEnglish,
        string? sku,
        UnitOfMeasure unit,
        Money defaultUnitPrice,
        VatCategory vatCategory,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        bool isArchived)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Catalog item ID cannot be empty.", nameof(id));
        }

        ValidateUtc(createdAtUtc, nameof(createdAtUtc));
        ArgumentOutOfRangeException.ThrowIfLessThan(updatedAtUtc, createdAtUtc);

        CatalogItem item = new(id)
        {
            CreatedAtUtc = createdAtUtc,
        };
        item.Update(nameArabic, nameEnglish, sku, unit, defaultUnitPrice, vatCategory, updatedAtUtc);
        item.IsArchived = isArchived;
        return item;
    }

    public void Update(
        string nameArabic,
        string? nameEnglish,
        string? sku,
        UnitOfMeasure unit,
        Money defaultUnitPrice,
        VatCategory vatCategory,
        DateTimeOffset updatedAtUtc)
    {
        ValidateMutationTime(updatedAtUtc);
        ArgumentOutOfRangeException.ThrowIfNegative(defaultUnitPrice.Halalah);
        if (string.IsNullOrWhiteSpace(unit.Value))
        {
            throw new ArgumentException("Unit of measure is required.", nameof(unit));
        }

        if (!Enum.IsDefined(vatCategory))
        {
            throw new ArgumentOutOfRangeException(nameof(vatCategory));
        }

        string validatedNameArabic = DomainTextRules.Required(
            nameArabic,
            DomainFieldLimits.PartyName,
            nameof(nameArabic));
        string? validatedNameEnglish = DomainTextRules.Optional(
            nameEnglish,
            DomainFieldLimits.PartyName,
            nameof(nameEnglish));
        string? validatedSku = DomainTextRules.Optional(sku, DomainFieldLimits.Sku, nameof(sku));
        string searchNameArabic = ArabicSearchNormalizer.Normalize(validatedNameArabic);
        string searchNameEnglish = ArabicSearchNormalizer.Normalize(validatedNameEnglish);
        string searchSku = ArabicSearchNormalizer.Normalize(validatedSku);

        NameArabic = validatedNameArabic;
        NameEnglish = validatedNameEnglish;
        SearchNameArabic = searchNameArabic;
        SearchNameEnglish = searchNameEnglish;
        Sku = validatedSku;
        SearchSku = searchSku;
        Unit = unit;
        DefaultUnitPrice = defaultUnitPrice;
        VatCategory = vatCategory;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void Archive(DateTimeOffset updatedAtUtc)
    {
        ValidateMutationTime(updatedAtUtc);
        IsArchived = true;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void Restore(DateTimeOffset updatedAtUtc)
    {
        ValidateMutationTime(updatedAtUtc);
        IsArchived = false;
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
        if (UpdatedAtUtc != default && value < UpdatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Update timestamp cannot precede the current version.");
        }
    }
}

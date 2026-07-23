using MHC.Invoicing.Domain.Catalog;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Domain.Tests.Catalog;

public sealed class CatalogItemTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ArchivedAt = CreatedAt.AddHours(1);

    [Fact]
    public void Create_PreservesArabicAndOptionalEnglishNames()
    {
        CatalogItem item = CatalogItem.Create(
            "خدمة استشارية",
            "Consulting service",
            "CONS-01",
            UnitOfMeasure.Create("ساعة"),
            Money.FromRiyals(250m),
            VatCategory.Standard15,
            CreatedAt);

        Assert.NotEqual(Guid.Empty, item.Id);
        Assert.Equal("خدمه استشاريه", item.SearchNameArabic);
        Assert.Equal("consulting service", item.SearchNameEnglish);
        Assert.Equal("cons-01", item.SearchSku);
        Assert.Equal("ساعة", item.Unit.Value);
        Assert.Equal(CreatedAt, item.CreatedAtUtc);
        Assert.Equal(CreatedAt, item.UpdatedAtUtc);
        Assert.False(item.IsArchived);
    }

    [Fact]
    public void Create_RejectsNegativePrice()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CatalogItem.Create(
            "خدمة",
            null,
            null,
            UnitOfMeasure.Create("وحدة"),
            new Money(-1),
            VatCategory.Standard15,
            CreatedAt));
    }

    [Fact]
    public void Archive_DoesNotDestroyCatalogItem()
    {
        CatalogItem item = CatalogItem.Create(
            "خدمة",
            null,
            null,
            UnitOfMeasure.Create("وحدة"),
            Money.Zero,
            VatCategory.ZeroRated,
            CreatedAt);

        item.Archive(ArchivedAt);

        Assert.True(item.IsArchived);
        Assert.Equal("خدمة", item.NameArabic);
        Assert.Equal(ArchivedAt, item.UpdatedAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("                                  ")]
    public void UnitOfMeasure_RejectsBlankOrOverlongValues(string value)
    {
        Assert.Throws<ArgumentException>(() => UnitOfMeasure.Create(value));
    }

    [Fact]
    public void Create_RejectsFieldsThatCannotFitPersistenceSchema()
    {
        Assert.Throws<ArgumentException>(() => CatalogItem.Create(
            new string('x', 201),
            null,
            null,
            UnitOfMeasure.Create("unit"),
            Money.Zero,
            VatCategory.Standard15,
            CreatedAt));
        Assert.Throws<ArgumentException>(() => CatalogItem.Create(
            "خدمة",
            null,
            new string('x', 65),
            UnitOfMeasure.Create("unit"),
            Money.Zero,
            VatCategory.Standard15,
            CreatedAt));
    }

    [Fact]
    public void Mutation_RejectsTimestampOlderThanCurrentVersion()
    {
        CatalogItem item = CatalogItem.Create(
            "خدمة",
            null,
            null,
            UnitOfMeasure.Create("unit"),
            Money.Zero,
            VatCategory.Standard15,
            CreatedAt);
        item.Archive(ArchivedAt);

        Assert.Throws<ArgumentOutOfRangeException>(() => item.Restore(CreatedAt.AddMinutes(30)));
        Assert.Equal(ArchivedAt, item.UpdatedAtUtc);
    }

    [Fact]
    public void Update_IsAtomicWhenALaterFieldIsInvalid()
    {
        CatalogItem item = CatalogItem.Create(
            "الخدمة الأصلية",
            "Original",
            "ORIGINAL",
            UnitOfMeasure.Create("unit"),
            Money.FromRiyals(10m),
            VatCategory.Standard15,
            CreatedAt);

        Assert.Throws<ArgumentException>(() => item.Update(
            "خدمة متغيرة",
            "Changed",
            "\uD800",
            UnitOfMeasure.Create("hour"),
            Money.FromRiyals(20m),
            VatCategory.Exempt,
            ArchivedAt));

        Assert.Equal("الخدمة الأصلية", item.NameArabic);
        Assert.Equal("Original", item.NameEnglish);
        Assert.Equal("ORIGINAL", item.Sku);
        Assert.Equal("unit", item.Unit.Value);
        Assert.Equal(Money.FromRiyals(10m), item.DefaultUnitPrice);
        Assert.Equal(VatCategory.Standard15, item.VatCategory);
        Assert.Equal(CreatedAt, item.UpdatedAtUtc);
    }
}

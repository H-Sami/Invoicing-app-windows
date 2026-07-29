using MHC.Invoicing.Domain.Customers;

namespace MHC.Invoicing.Domain.Tests.Customers;

public sealed class CustomerTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = CreatedAt.AddHours(1);

    [Fact]
    public void Create_NormalizesNameAndIdentifiersForSearch()
    {
        Customer customer = Customer.Create(
            "شركة آفاق التقنية",
            "Afaq Technology",
            "123",
            "45",
            "الرياض",
            "+966****0000",
            "billing@example.com",
            CreatedAt);

        Assert.NotEqual(Guid.Empty, customer.Id);
        Assert.Equal("شركه افاق التقنيه", customer.SearchNameArabic);
        Assert.Equal("afaq technology", customer.SearchNameEnglish);
        Assert.Equal("123", customer.VatNumber);
        Assert.Equal("45", customer.CommercialRegistration);
        Assert.Equal(CreatedAt, customer.CreatedAtUtc);
        Assert.Equal(CreatedAt, customer.UpdatedAtUtc);
        Assert.False(customer.IsArchived);
    }

    [Fact]
    public void Update_RebuildsSearchKeysWithoutChangingIdentity()
    {
        Customer customer = Customer.Create("عميل", null, null, null, null, null, null, CreatedAt);
        Guid id = customer.Id;

        customer.Update("إتقان", "ITQAN", null, null, null, null, null, UpdatedAt);

        Assert.Equal(id, customer.Id);
        Assert.Equal("اتقان", customer.SearchNameArabic);
        Assert.Equal("itqan", customer.SearchNameEnglish);
        Assert.Equal(UpdatedAt, customer.UpdatedAtUtc);
    }

    [Fact]
    public void Create_RejectsNonNumericVatNumber()
    {
        Assert.Throws<ArgumentException>(() => Customer.Create(
            "عميل", null, "12A", null, null, null, null, CreatedAt));
    }

    [Fact]
    public void Archive_MakesCustomerUnavailableWithoutDeletingIt()
    {
        Customer customer = Customer.Create("عميل", null, null, null, null, null, null, CreatedAt);

        customer.Archive(UpdatedAt);

        Assert.True(customer.IsArchived);
        Assert.Equal(UpdatedAt, customer.UpdatedAtUtc);
    }

    [Fact]
    public void Create_RejectsFieldsThatCannotFitPersistenceSchema()
    {
        Assert.Throws<ArgumentException>(() => Customer.Create(
            new string('x', 201), null, null, null, null, null, null, CreatedAt));
        Assert.Throws<ArgumentException>(() => Customer.Create(
            "عميل", null, null, null, null, null, new string('x', 255), CreatedAt));
    }

    [Fact]
    public void Mutation_RejectsTimestampOlderThanCurrentVersion()
    {
        Customer customer = Customer.Create("عميل", null, null, null, null, null, null, CreatedAt);
        customer.Update("أحدث", null, null, null, null, null, null, UpdatedAt);

        Assert.Throws<ArgumentOutOfRangeException>(() => customer.Archive(CreatedAt.AddMinutes(30)));
        Assert.Equal(UpdatedAt, customer.UpdatedAtUtc);
    }

    [Fact]
    public void Update_IsAtomicWhenALaterFieldIsInvalid()
    {
        Customer customer = Customer.Create(
            "الاسم الأصلي",
            "Original",
            null,
            null,
            null,
            null,
            "original@example.com",
            CreatedAt);

        Assert.Throws<ArgumentException>(() => customer.Update(
            "اسم متغير",
            "\uD800",
            null,
            null,
            null,
            null,
            "changed@example.com",
            UpdatedAt));

        Assert.Equal("الاسم الأصلي", customer.NameArabic);
        Assert.Equal("Original", customer.NameEnglish);
        Assert.Equal("original@example.com", customer.Email);
        Assert.Equal(CreatedAt, customer.UpdatedAtUtc);
    }
}

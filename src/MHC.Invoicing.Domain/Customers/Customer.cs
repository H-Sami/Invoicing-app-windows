using MHC.Invoicing.Domain.Search;
using MHC.Invoicing.Domain.Validation;

namespace MHC.Invoicing.Domain.Customers;

public sealed class Customer
{
    private Customer()
    {
        NameArabic = null!;
        SearchNameArabic = null!;
    }

    private Customer(Guid id)
        : this()
    {
        Id = id;
    }

    public Guid Id { get; private set; }

    public string NameArabic { get; private set; }

    public string? NameEnglish { get; private set; }

    public string SearchNameArabic { get; private set; }

    public string SearchNameEnglish { get; private set; } = string.Empty;

    public string? VatNumber { get; private set; }

    public string? CommercialRegistration { get; private set; }

    public string? Address { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public bool IsArchived { get; private set; }

    public static Customer Create(
        string nameArabic,
        string? nameEnglish,
        string? vatNumber,
        string? commercialRegistration,
        string? address,
        string? phone,
        string? email,
        DateTimeOffset createdAtUtc)
    {
        ValidateUtc(createdAtUtc, nameof(createdAtUtc));
        Customer customer = new(Guid.CreateVersion7())
        {
            CreatedAtUtc = createdAtUtc,
        };
        customer.Update(nameArabic, nameEnglish, vatNumber, commercialRegistration, address, phone, email, createdAtUtc);
        return customer;
    }

    public void Update(
        string nameArabic,
        string? nameEnglish,
        string? vatNumber,
        string? commercialRegistration,
        string? address,
        string? phone,
        string? email,
        DateTimeOffset updatedAtUtc)
    {
        ValidateMutationTime(updatedAtUtc);
        string validatedNameArabic = DomainTextRules.Required(
            nameArabic,
            DomainFieldLimits.PartyName,
            nameof(nameArabic));
        string? validatedNameEnglish = DomainTextRules.Optional(
            nameEnglish,
            DomainFieldLimits.PartyName,
            nameof(nameEnglish));
        string? validatedVatNumber = DomainTextRules.OptionalDigits(vatNumber, 15, nameof(vatNumber));
        string? validatedCommercialRegistration = DomainTextRules.OptionalDigits(
            commercialRegistration,
            DomainFieldLimits.CommercialRegistration,
            nameof(commercialRegistration));
        string? validatedAddress = DomainTextRules.Optional(address, DomainFieldLimits.Address, nameof(address));
        string? validatedPhone = DomainTextRules.Optional(phone, DomainFieldLimits.Phone, nameof(phone));
        string? validatedEmail = DomainTextRules.Optional(email, DomainFieldLimits.Email, nameof(email));
        string searchNameArabic = ArabicSearchNormalizer.Normalize(validatedNameArabic);
        string searchNameEnglish = ArabicSearchNormalizer.Normalize(validatedNameEnglish);

        NameArabic = validatedNameArabic;
        NameEnglish = validatedNameEnglish;
        SearchNameArabic = searchNameArabic;
        SearchNameEnglish = searchNameEnglish;
        VatNumber = validatedVatNumber;
        CommercialRegistration = validatedCommercialRegistration;
        Address = validatedAddress;
        Phone = validatedPhone;
        Email = validatedEmail;
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

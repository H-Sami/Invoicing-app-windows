namespace MHC.Invoicing.Application.Customers;

public sealed record CustomerSuggestion(
    Guid Id,
    string NameArabic,
    string? NameEnglish,
    string? VatNumber,
    string? CommercialRegistration,
    string? Address,
    string? Phone,
    string? Email);

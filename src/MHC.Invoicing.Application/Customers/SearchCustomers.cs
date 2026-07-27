using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Customers;
using MHC.Invoicing.Domain.Search;

namespace MHC.Invoicing.Application.Customers;

public sealed class SearchCustomers(ICustomerRepository repository)
{
    private const int MaximumSuggestions = 8;
    private const int RepositoryCandidateLimit = 40;

    public async Task<IReadOnlyList<CustomerSuggestion>> ExecuteAsync(
        string? searchText,
        CancellationToken cancellationToken = default)
    {
        string rawSearch = searchText?.Trim() ?? string.Empty;
        string normalizedSearch = ArabicSearchNormalizer.Normalize(rawSearch);
        bool identifierSearch = rawSearch.Length > 0 && rawSearch.All(char.IsAsciiDigit);
        if (normalizedSearch.Length < 2 && !identifierSearch)
        {
            return Array.Empty<CustomerSuggestion>();
        }

        IReadOnlyList<VersionedCustomer> candidates = await repository.SearchAsync(
            rawSearch,
            includeArchived: false,
            RepositoryCandidateLimit,
            cancellationToken);

        return candidates
            .Select(candidate => new RankedCustomer(candidate.Customer, Rank(candidate.Customer, rawSearch, normalizedSearch)))
            .Where(candidate => candidate.Rank < int.MaxValue)
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.Customer.NameArabic, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Customer.Id)
            .Take(MaximumSuggestions)
            .Select(candidate => ToSuggestion(candidate.Customer))
            .ToArray();
    }

    private static int Rank(Customer customer, string rawSearch, string normalizedSearch)
    {
        if (string.Equals(customer.VatNumber, rawSearch, StringComparison.Ordinal) ||
            string.Equals(customer.CommercialRegistration, rawSearch, StringComparison.Ordinal))
        {
            return 0;
        }

        if (customer.SearchNameArabic.StartsWith(normalizedSearch, StringComparison.Ordinal) ||
            customer.SearchNameEnglish.StartsWith(normalizedSearch, StringComparison.Ordinal) ||
            StartsWith(customer.VatNumber, rawSearch) ||
            StartsWith(customer.CommercialRegistration, rawSearch))
        {
            return 1;
        }

        if (customer.SearchNameArabic.Contains(normalizedSearch, StringComparison.Ordinal) ||
            customer.SearchNameEnglish.Contains(normalizedSearch, StringComparison.Ordinal) ||
            Contains(customer.VatNumber, rawSearch) ||
            Contains(customer.CommercialRegistration, rawSearch) ||
            Contains(customer.Phone, rawSearch))
        {
            return 2;
        }

        return int.MaxValue;
    }

    private static bool StartsWith(string? value, string search) =>
        search.Length > 0 && value?.StartsWith(search, StringComparison.Ordinal) == true;

    private static bool Contains(string? value, string search) =>
        search.Length > 0 && value?.Contains(search, StringComparison.Ordinal) == true;

    private static CustomerSuggestion ToSuggestion(Customer customer) => new(
        customer.Id,
        customer.NameArabic,
        customer.NameEnglish,
        customer.VatNumber,
        customer.CommercialRegistration,
        customer.Address,
        customer.Phone,
        customer.Email);

    private sealed record RankedCustomer(Customer Customer, int Rank);
}

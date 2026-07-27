using MHC.Invoicing.Application.Customers;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Customers;

namespace MHC.Invoicing.Application.Tests.Customers;

public sealed class SearchCustomersTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 23, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_RanksExactIdentifiersThenPrefixThenContains()
    {
        Customer contains = Create("شركة آفاق", "Afaq Company", null, null);
        Customer prefix = Create("آفاق المباشرة", null, null, null);
        Customer exactVat = Create("عميل ضريبي", null, "310123456789003", null);
        FakeCustomerRepository repository = new([contains, prefix, exactVat]);
        SearchCustomers useCase = new(repository);

        IReadOnlyList<CustomerSuggestion> byName = await useCase.ExecuteAsync(
            "افاق",
            TestContext.Current.CancellationToken);
        IReadOnlyList<CustomerSuggestion> byVat = await useCase.ExecuteAsync(
            "310123456789003",
            TestContext.Current.CancellationToken);

        Assert.Equal([prefix.Id, contains.Id], byName.Select(suggestion => suggestion.Id));
        Assert.Equal(exactVat.Id, byVat[0].Id);
        Assert.All(byName, suggestion => Assert.NotNull(suggestion.NameArabic));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ا")]
    [InlineData(" ")]
    public async Task ExecuteAsync_DoesNotQueryForFewerThanTwoUsefulNameCharacters(string query)
    {
        FakeCustomerRepository repository = new([]);
        SearchCustomers useCase = new(repository);

        Assert.Empty(await useCase.ExecuteAsync(query, TestContext.Current.CancellationToken));
        Assert.Equal(0, repository.SearchCalls);
    }

    [Fact]
    public async Task ExecuteAsync_AllowsIdentifierDigitsAndLimitsSuggestionsToEight()
    {
        Customer[] customers = Enumerable.Range(0, 10)
            .Select(index => Create($"عميل {index}", null, $"3{index:00000000000000}", null))
            .ToArray();
        FakeCustomerRepository repository = new(customers);
        SearchCustomers useCase = new(repository);

        IReadOnlyList<CustomerSuggestion> results = await useCase.ExecuteAsync(
            "3",
            TestContext.Current.CancellationToken);

        Assert.Equal(8, results.Count);
        Assert.Equal(1, repository.SearchCalls);
    }

    private static Customer Create(
        string nameArabic,
        string? nameEnglish,
        string? vatNumber,
        string? commercialRegistration) =>
        Customer.Create(
            nameArabic,
            nameEnglish,
            vatNumber,
            commercialRegistration,
            "الرياض",
            null,
            null,
            CreatedAt);

    private sealed class FakeCustomerRepository(IEnumerable<Customer> customers) : ICustomerRepository
    {
        private readonly IReadOnlyList<VersionedCustomer> _customers = customers
            .Select(customer => new VersionedCustomer(customer, 0))
            .ToArray();

        public int SearchCalls { get; private set; }

        public Task<VersionedCustomer> AddAsync(Customer customer, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VersionedCustomer?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VersionedCustomer>> SearchAsync(
            string? searchText,
            bool includeArchived,
            int limit,
            CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            return Task.FromResult(_customers);
        }

        public Task<VersionedCustomer> UpdateAsync(
            Customer customer,
            int expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

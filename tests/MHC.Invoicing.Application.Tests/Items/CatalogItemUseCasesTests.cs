using MHC.Invoicing.Application.Abstractions;
using MHC.Invoicing.Application.Items;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Catalog;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Application.Tests.Items;

public sealed class CatalogItemUseCasesTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Commands_CreateUpdateArchiveRestoreAndSelectReusableItem()
    {
        FakeCatalogItemRepository repository = new();
        FixedClock clock = new(Now);
        CreateCatalogItem create = new(repository, clock);
        VersionedCatalogItem created = await create.ExecuteAsync(
            new CatalogItemCommand(
                "خدمة صيانة",
                "Maintenance",
                "MNT-01",
                "hour",
                Money.FromRiyals(150m),
                VatCategory.Standard15),
            TestContext.Current.CancellationToken);

        UpdateCatalogItem update = new(repository, clock);
        clock.UtcNow = Now.AddMinutes(1);
        VersionedCatalogItem updated = await update.ExecuteAsync(
            created.CatalogItem.Id,
            created.Revision,
            new CatalogItemCommand(
                "خدمة صيانة محدثة",
                "Updated Maintenance",
                "MNT-02",
                "day",
                Money.FromRiyals(500m),
                VatCategory.Exempt),
            TestContext.Current.CancellationToken);

        ArchiveCatalogItem archive = new(repository, clock);
        clock.UtcNow = Now.AddMinutes(2);
        VersionedCatalogItem archived = await archive.ExecuteAsync(
            updated.CatalogItem.Id,
            updated.Revision,
            TestContext.Current.CancellationToken);
        Assert.True(archived.CatalogItem.IsArchived);

        RestoreCatalogItem restore = new(repository, clock);
        clock.UtcNow = Now.AddMinutes(3);
        VersionedCatalogItem restored = await restore.ExecuteAsync(
            archived.CatalogItem.Id,
            archived.Revision,
            TestContext.Current.CancellationToken);
        Assert.False(restored.CatalogItem.IsArchived);

        SelectCatalogItem select = new(repository);
        InvoiceDraftLine line = await select.ExecuteAsync(
            restored.CatalogItem.Id,
            TestContext.Current.CancellationToken);

        Assert.Equal(restored.CatalogItem.Id, line.CatalogItemId);
        Assert.Equal("خدمة صيانة محدثة", line.Description);
        Assert.Equal("MNT-02", line.Sku);
        Assert.Equal("day", line.Unit);
        Assert.Equal(1m, line.Quantity);
        Assert.Equal(Money.FromRiyals(500m), line.UnitPrice);
        Assert.Equal(VatCategory.Exempt, line.VatCategory);
        Assert.NotEqual(Guid.Empty, line.Id);
    }

    [Fact]
    public async Task Search_ReturnsAtMostTwentyActiveItems()
    {
        FakeCatalogItemRepository repository = new();
        for (int index = 0; index < 25; index++)
        {
            await repository.AddAsync(
                CatalogItem.Create(
                    $"خدمة {index}",
                    null,
                    $"SKU-{index:00}",
                    UnitOfMeasure.Create("unit"),
                    Money.FromRiyals(index),
                    VatCategory.Standard15,
                    Now),
                TestContext.Current.CancellationToken);
        }

        SearchCatalogItems search = new(repository);
        IReadOnlyList<CatalogItemSuggestion> results = await search.ExecuteAsync(
            "خدمة",
            TestContext.Current.CancellationToken);

        Assert.Equal(20, results.Count);
        Assert.All(results, result => Assert.False(result.IsArchived));
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public DateTimeOffset SaudiNow => UtcNow.ToOffset(TimeSpan.FromHours(3));
    }

    private sealed class FakeCatalogItemRepository : ICatalogItemRepository
    {
        private readonly Dictionary<Guid, VersionedCatalogItem> _items = [];

        public Task<VersionedCatalogItem> AddAsync(
            CatalogItem catalogItem,
            CancellationToken cancellationToken = default)
        {
            VersionedCatalogItem result = new(catalogItem, 0);
            _items.Add(catalogItem.Id, result);
            return Task.FromResult(result);
        }

        public Task<VersionedCatalogItem?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.GetValueOrDefault(id));

        public Task<IReadOnlyList<VersionedCatalogItem>> SearchAsync(
            string? searchText,
            bool includeArchived,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VersionedCatalogItem>>(
                _items.Values.Where(item => includeArchived || !item.CatalogItem.IsArchived).Take(limit).ToArray());

        public Task<VersionedCatalogItem> UpdateAsync(
            CatalogItem catalogItem,
            int expectedRevision,
            CancellationToken cancellationToken = default)
        {
            VersionedCatalogItem current = _items[catalogItem.Id];
            if (current.Revision != expectedRevision)
            {
                throw new InvalidOperationException("Revision mismatch.");
            }

            VersionedCatalogItem updated = new(catalogItem, checked(expectedRevision + 1));
            _items[catalogItem.Id] = updated;
            return Task.FromResult(updated);
        }
    }
}

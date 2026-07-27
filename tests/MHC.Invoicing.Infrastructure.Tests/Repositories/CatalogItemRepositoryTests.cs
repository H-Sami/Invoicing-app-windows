using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Catalog;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Tests.Repositories;

public sealed class CatalogItemRepositoryTests
{
    [Fact]
    public async Task AddGetSearchUpdateAndArchive_RoundTripWithOptimisticConcurrency()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using MhcDbContext context = new(options);
            await context.Database.MigrateAsync(cancellationToken);
            CatalogItemRepository repository = new(context);
            DateTimeOffset createdAt = new(2026, 7, 23, 1, 0, 0, TimeSpan.Zero);
            CatalogItem item = CatalogItem.Create(
                "استضافة مواقع",
                "Web Hosting",
                "HOST-01",
                UnitOfMeasure.Create("month"),
                Money.FromRiyals(100m),
                VatCategory.Standard15,
                createdAt);

            VersionedCatalogItem added = await repository.AddAsync(item, cancellationToken);
            CatalogItem containsItem = CatalogItem.Create(
                "خدمات استضافة",
                null,
                "SERVICE-01",
                UnitOfMeasure.Create("month"),
                Money.FromRiyals(50m),
                VatCategory.Standard15,
                createdAt);
            await repository.AddAsync(containsItem, cancellationToken);
            VersionedCatalogItem? loaded = await repository.GetAsync(item.Id, cancellationToken);
            IReadOnlyList<VersionedCatalogItem> matches = await repository.SearchAsync(
                "استضافه",
                includeArchived: false,
                limit: 20,
                cancellationToken);

            Assert.Equal(0, added.Revision);
            Assert.NotNull(loaded);
            Assert.Equal("HOST-01", loaded.CatalogItem.Sku);
            Assert.Equal(Money.FromRiyals(100m), loaded.CatalogItem.DefaultUnitPrice);
            Assert.Contains(matches, match => match.CatalogItem.Id == item.Id);
            Assert.Equal(item.Id, matches[0].CatalogItem.Id);

            item.Update(
                "استضافة محدثة",
                "Updated Hosting",
                "HOST-02",
                UnitOfMeasure.Create("year"),
                Money.FromRiyals(900m),
                VatCategory.Standard15,
                createdAt.AddMinutes(1));
            VersionedCatalogItem updated = await repository.UpdateAsync(item, 0, cancellationToken);
            Assert.Equal(1, updated.Revision);
            await Assert.ThrowsAsync<PersistenceConcurrencyException>(() =>
                repository.UpdateAsync(item, 0, cancellationToken));

            item.Archive(createdAt.AddMinutes(2));
            await repository.UpdateAsync(item, 1, cancellationToken);
            Assert.Empty(await repository.SearchAsync("host-02", false, 20, cancellationToken));
            Assert.Single(await repository.SearchAsync("host-02", true, 20, cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                string path = databasePath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}

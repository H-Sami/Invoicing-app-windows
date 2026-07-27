using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Customers;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Tests.Repositories;

public sealed class CustomerRepositoryTests
{
    [Fact]
    public async Task AddGetSearchUpdateAndArchive_RoundTripWithOptimisticConcurrency()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            DbContextOptions<MhcDbContext> options = CreateOptions(databasePath);
            await using MhcDbContext context = new(options);
            await context.Database.MigrateAsync(cancellationToken);
            CustomerRepository repository = new(context);
            DateTimeOffset createdAt = new(2026, 7, 23, 1, 0, 0, TimeSpan.Zero);
            Customer customer = Customer.Create(
                "شركة آفاق التقنية",
                "Afaq Technology",
                "310123456789003",
                "1010123456",
                "الرياض",
                "+966500000000",
                "billing@example.com",
                createdAt);

            VersionedCustomer added = await repository.AddAsync(customer, cancellationToken);
            Customer prefixCustomer = Customer.Create(
                "آفاق المباشرة",
                null,
                null,
                null,
                null,
                null,
                null,
                createdAt);
            await repository.AddAsync(prefixCustomer, cancellationToken);
            VersionedCustomer? loaded = await repository.GetAsync(customer.Id, cancellationToken);
            IReadOnlyList<VersionedCustomer> matches = await repository.SearchAsync(
                "افاق",
                includeArchived: false,
                limit: 20,
                cancellationToken);

            Assert.Equal(0, added.Revision);
            Assert.NotNull(loaded);
            Assert.Equal(customer.Id, loaded.Customer.Id);
            Assert.Equal("شركة آفاق التقنية", loaded.Customer.NameArabic);
            Assert.Contains(matches, match => match.Customer.Id == customer.Id);
            Assert.Equal(prefixCustomer.Id, matches[0].Customer.Id);

            customer.Update(
                "شركة آفاق المحدثة",
                "Updated Afaq",
                customer.VatNumber,
                customer.CommercialRegistration,
                customer.Address,
                customer.Phone,
                customer.Email,
                createdAt.AddMinutes(1));
            VersionedCustomer updated = await repository.UpdateAsync(customer, expectedRevision: 0, cancellationToken);
            Assert.Equal(1, updated.Revision);
            await Assert.ThrowsAsync<PersistenceConcurrencyException>(() =>
                repository.UpdateAsync(customer, expectedRevision: 0, cancellationToken));

            customer.Archive(createdAt.AddMinutes(2));
            await repository.UpdateAsync(customer, expectedRevision: 1, cancellationToken);
            IReadOnlyList<VersionedCustomer> activeMatches =
                await repository.SearchAsync("افاق", false, 20, cancellationToken);
            Assert.Single(activeMatches);
            Assert.Equal(prefixCustomer.Id, activeMatches[0].Customer.Id);
            Assert.Single(await repository.SearchAsync("afaq", true, 20, cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static DbContextOptions<MhcDbContext> CreateOptions(string databasePath) =>
        new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

    private static void DeleteDatabaseFiles(string databasePath)
    {
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

using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Tests.Repositories;

public sealed class CompanyProfileRepositoryTests
{
    [Fact]
    public async Task SaveAsync_CreatesUpdatesAndRejectsAStaleRevision()
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
            CompanyProfileRepository repository = new(context);
            byte[] logo = [0x89, 0x50, 0x4e, 0x47];
            CompanyProfileSettings initial = new(
                "تقنية إم إتش سي",
                "MHC Technology",
                "123",
                "45",
                "الفرع الرئيسي",
                "الرياض، المملكة العربية السعودية",
                "المشغل الرئيسي",
                PaymentMethod.Card,
                logo,
                "image/png");

            VersionedCompanyProfile created = await repository.SaveAsync(initial, expectedRevision: null, cancellationToken);
            VersionedCompanyProfile? loaded = await repository.GetAsync(cancellationToken);

            Assert.Equal(0, created.Revision);
            Assert.Equal(PaymentMethod.Card, loaded?.Profile.DefaultPaymentMethod);
            Assert.Equal("123", loaded?.Profile.VatNumber);
            Assert.Equal("45", loaded?.Profile.CommercialRegistration);
            Assert.Equal(logo, loaded?.Profile.LogoBytes);
            Assert.Equal("image/png", loaded?.Profile.LogoMimeType);
            CompanyProfileSettings updated = initial with { OperatorName = "مشغل محدث" };
            VersionedCompanyProfile saved = await repository.SaveAsync(updated, expectedRevision: 0, cancellationToken);
            Assert.Equal(1, saved.Revision);
            Assert.Equal("مشغل محدث", (await repository.GetAsync(cancellationToken))?.Profile.OperatorName);

            await Assert.ThrowsAsync<PersistenceConcurrencyException>(() =>
                repository.SaveAsync(updated, expectedRevision: 0, cancellationToken));
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

    [Theory]
    [InlineData("", "310123456789003", "الفرع", "العنوان", "المشغل")]
    [InlineData("شركة", "12A", "الفرع", "العنوان", "المشغل")]
    [InlineData("شركة", "310123456789003", "", "العنوان", "المشغل")]
    [InlineData("شركة", "310123456789003", "الفرع", "", "المشغل")]
    [InlineData("شركة", "310123456789003", "الفرع", "العنوان", "")]
    public async Task SaveAsync_RejectsInvalidSellerIdentity(
        string nameArabic,
        string vatNumber,
        string branch,
        string address,
        string operatorName)
    {
        await using MhcDbContext context = new(new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options);
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        CompanyProfileRepository repository = new(context);
        CompanyProfileSettings invalid = new(
            nameArabic,
            null,
            vatNumber,
            null,
            branch,
            address,
            operatorName,
            PaymentMethod.Cash);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            repository.SaveAsync(invalid, expectedRevision: null, TestContext.Current.CancellationToken));
    }
}

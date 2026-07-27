using MHC.Invoicing.Application.Drafts;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Tests.Repositories;

public sealed class DraftRepositoryTests
{
    [Fact]
    public async Task LoadAsyncReturnsNewestDraftsFirstWithPickerDetails()
    {
        DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using MhcDbContext context = new(options);
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        DraftRepository repository = new(context);
        DraftRecord older = CreateDraft(
            new DateTimeOffset(2026, 7, 22, 7, 0, 0, TimeSpan.Zero), []) with
        {
            Customer = new DraftParty("الأقدم", "Older", null, null, null),
            UpdatedAtUtc = new DateTimeOffset(2026, 7, 22, 8, 0, 0, TimeSpan.Zero),
        };
        DraftRecord newer = CreateDraft(
            new DateTimeOffset(2026, 7, 23, 7, 0, 0, TimeSpan.Zero), [CreateLine("خدمة", 1)]) with
        {
            Customer = new DraftParty("الأحدث", "Newest", null, null, null),
            UpdatedAtUtc = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero),
        };
        await repository.SaveAsync(older, null, TestContext.Current.CancellationToken);
        await repository.SaveAsync(newer, null, TestContext.Current.CancellationToken);

        IReadOnlyList<MHC.Invoicing.Application.Workflows.ResumableDraft> result =
            await repository.LoadAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal([newer.Id, older.Id], result.Select(draft => draft.Id));
        Assert.Equal("الأحدث", result[0].CustomerName);
        Assert.Equal(newer.Lines.Count, result[0].LineCount);
    }
    [Fact]
    public async Task SaveGetReplaceLinesAndDelete_UsesOptimisticRevisionsWithoutLegalIdentifiers()
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
            DraftRepository repository = new(context);
            DateTimeOffset createdAt = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);
            DraftRecord draft = CreateDraft(createdAt, [CreateLine("الخدمة الأولى", 1)]);

            VersionedDraft created = await repository.SaveAsync(draft, null, cancellationToken);
            Assert.Equal(0, created.Revision);
            VersionedDraft? loaded = await repository.GetAsync(draft.Id, cancellationToken);
            Assert.NotNull(loaded);
            Assert.Single(loaded.Draft.Lines);

            DraftRecord replacement = draft with
            {
                UpdatedAtUtc = createdAt.AddMinutes(1),
                Lines = [CreateLine("الخدمة البديلة", 2)],
            };
            VersionedDraft updated = await repository.SaveAsync(replacement, 0, cancellationToken);
            Assert.Equal(1, updated.Revision);
            VersionedDraft? replaced = await repository.GetAsync(draft.Id, cancellationToken);
            Assert.NotNull(replaced);
            Assert.Single(replaced.Draft.Lines);
            Assert.Equal("الخدمة البديلة", replaced.Draft.Lines[0].Description);
            Assert.Equal(2m, replaced.Draft.Lines[0].Quantity);

            await Assert.ThrowsAsync<PersistenceConcurrencyException>(() =>
                repository.SaveAsync(replacement, 0, cancellationToken));
            await Assert.ThrowsAsync<PersistenceConcurrencyException>(() =>
                repository.DeleteAsync(draft.Id, 0, cancellationToken));
            await repository.DeleteAsync(draft.Id, 1, cancellationToken);
            Assert.Null(await repository.GetAsync(draft.Id, cancellationToken));
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

    private static DraftRecord CreateDraft(DateTimeOffset createdAt, IReadOnlyList<InvoiceDraftLine> lines) => new(
        Guid.CreateVersion7(),
        InvoiceDocumentType.TaxInvoice,
        null,
        null,
        new DateOnly(2026, 7, 23),
        new DraftParty("عميل نقدي", null, null, null, "الرياض"),
        PaymentMethod.Cash,
        "فاتورة خدمات",
        "ملاحظات",
        true,
        lines,
        createdAt,
        createdAt);

    private static InvoiceDraftLine CreateLine(string description, decimal quantity) => new(
        Guid.CreateVersion7(),
        null,
        description,
        null,
        "unit",
        quantity,
        Money.FromRiyals(100m),
        VatCategory.Standard15,
        null,
        null);
}

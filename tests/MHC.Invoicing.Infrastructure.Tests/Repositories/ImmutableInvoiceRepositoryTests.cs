using System.Security.Cryptography;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.Search;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using MHC.Invoicing.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Tests.Repositories;

public sealed class ImmutableInvoiceRepositoryTests
{
    [Fact]
    public async Task IncompleteInvoice_IsNotVisibleAndCannotBeVoided()
    {
        await WithDatabaseAsync(async (context, cancellationToken) =>
        {
            InvoiceEntity entity = CreateInvoice();
            context.Invoices.Add(entity);
            await context.SaveChangesAsync(cancellationToken);
            InvoiceRepository repository = new(context);

            Assert.Null(await repository.GetSummaryAsync(entity.Id, cancellationToken));
            Assert.Null(await repository.GetSnapshotAsync(entity.Id, cancellationToken));
            await Assert.ThrowsAsync<InvoiceNotFoundException>(() => repository.VoidAsync(
                entity.Id,
                "Reason",
                "Operator",
                DateTimeOffset.FromUnixTimeMilliseconds(5_000),
                cancellationToken));
        });
    }

    [Fact]
    public async Task GetSnapshot_ReturnsEveryIssuedValueAndOrderedLines()
    {
        await WithDatabaseAsync(async (context, cancellationToken) =>
        {
            InvoiceEntity entity = CreateInvoice();
            entity.Lines.Add(CreateLine(entity.Id, 1, "Second"));
            entity.Lines.Add(CreateLine(entity.Id, 0, "First"));
            await SaveAndFinalizeAsync(context, entity, cancellationToken);
            context.ChangeTracker.Clear();

            InvoiceSnapshot? snapshot = await new InvoiceRepository(context).GetSnapshotAsync(entity.Id, cancellationToken);

            Assert.NotNull(snapshot);
            Assert.Equal(entity.PublicNumber, snapshot.PublicNumber);
            Assert.Equal(entity.SellerNameArabic, snapshot.Seller.NameArabic);
            Assert.Equal(entity.CustomerNameEnglish, snapshot.Customer.NameEnglish);
            Assert.Equal(entity.ShowNotes, snapshot.ShowNotes);
            Assert.Equal(["First", "Second"], snapshot.Lines.Select(line => line.Description));
            Assert.Equal(2m, snapshot.Lines[0].Quantity);
            Assert.Null(snapshot.Void);
        });
    }

    [Fact]
    public async Task GetDocument_VerifiesHashAndReturnsDefensiveCopies()
    {
        await WithDatabaseAsync(async (context, cancellationToken) =>
        {
            InvoiceEntity entity = CreateInvoice();
            byte[] pdf = "%PDF-1.7 immutable"u8.ToArray();
            entity.Document = new InvoiceDocumentEntity
            {
                InvoiceId = entity.Id,
                PdfBytes = pdf,
                Sha256 = SHA256.HashData(pdf),
                ByteLength = pdf.Length,
                MimeType = "application/pdf",
                CreatedAtUtcMs = entity.IssuedAtUtcMs,
            };
            await SaveAndFinalizeAsync(context, entity, cancellationToken);
            context.ChangeTracker.Clear();
            InvoiceRepository repository = new(context);

            InvoiceDocument? document = await repository.GetDocumentAsync(entity.Id, cancellationToken);
            byte[] firstRead = document!.PdfBytes;
            firstRead[0] = 0;

            Assert.Equal((byte)'%', document.PdfBytes[0]);

            await context.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER trg_invoice_documents_no_update",
                cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE invoice_documents SET sha256 = zeroblob(32) WHERE invoice_id = {0}",
                [entity.Id], cancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetDocumentAsync(entity.Id, cancellationToken));
        });
    }

    [Fact]
    public async Task Void_IsFirstOnlyAndAtomicallyAppendsOneAuditEvent()
    {
        await WithDatabaseAsync(async (context, cancellationToken) =>
        {
            InvoiceEntity entity = CreateInvoice();
            await SaveAndFinalizeAsync(context, entity, cancellationToken);
            context.ChangeTracker.Clear();
            InvoiceRepository repository = new(context);
            DateTimeOffset at = DateTimeOffset.FromUnixTimeMilliseconds(5_000);

            InvoiceVoidInfo result = await repository.VoidAsync(entity.Id, "Duplicate", "Operator", at, cancellationToken);
            await Assert.ThrowsAsync<InvoiceAlreadyVoidedException>(
                () => repository.VoidAsync(entity.Id, "Again", "Operator", at.AddSeconds(1), cancellationToken));

            Assert.Equal(new InvoiceVoidInfo("Duplicate", at, "Operator"), result);
            InvoiceSnapshot snapshot = Assert.IsType<InvoiceSnapshot>(
                await repository.GetSnapshotAsync(entity.Id, cancellationToken));
            Assert.Equal(result, snapshot.Void);
            Assert.Equal(1, await context.InvoiceVoids.CountAsync(cancellationToken));
            AuditEventEntity audit = await context.AuditEvents.SingleAsync(
                eventEntity => eventEntity.EventType == 3,
                cancellationToken);
            Assert.Equal(2, await context.AuditEvents.CountAsync(cancellationToken));
            Assert.Equal(entity.Id, audit.InvoiceId);
            Assert.Equal(3, audit.EventType);
            Assert.Contains("Duplicate", audit.DetailsJson, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Void_RejectsMissingInvoiceWithoutAuditEvent()
    {
        await WithDatabaseAsync(async (context, cancellationToken) =>
        {
            InvoiceRepository repository = new(context);

            await Assert.ThrowsAsync<InvoiceNotFoundException>(() => repository.VoidAsync(
                Guid.NewGuid(), "Reason", "Operator", DateTimeOffset.FromUnixTimeMilliseconds(5_000), cancellationToken));

            Assert.Empty(await context.AuditEvents.ToListAsync(cancellationToken));
        });
    }

    private static async Task SaveAndFinalizeAsync(
        MhcDbContext context,
        InvoiceEntity entity,
        CancellationToken cancellationToken)
    {
        if (entity.Lines.Count == 0)
        {
            entity.Lines.Add(CreateLine(entity.Id, 0, "Service"));
        }

        if (entity.Document is null)
        {
            byte[] pdf = "%PDF-1.7 immutable"u8.ToArray();
            entity.Document = new InvoiceDocumentEntity
            {
                InvoiceId = entity.Id,
                PdfBytes = pdf,
                Sha256 = SHA256.HashData(pdf),
                ByteLength = pdf.Length,
                MimeType = "application/pdf",
                CreatedAtUtcMs = entity.IssuedAtUtcMs,
            };
        }

        entity.SubtotalHalalah = entity.Lines.Sum(line => line.NetHalalah);
        entity.VatHalalah = entity.Lines.Sum(line => line.VatHalalah);
        entity.GrandTotalHalalah = entity.Lines.Sum(line => line.GrossHalalah);
        context.Invoices.Add(entity);
        context.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            InvoiceId = entity.Id,
            EventType = 1,
            OccurredAtUtcMs = entity.IssuedAtUtcMs,
            OperatorName = entity.OperatorName,
        });
        await context.SaveChangesAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({entity.Id}, {entity.IssuedAtUtcMs});",
            cancellationToken);
    }

    private static async Task WithDatabaseAsync(Func<MhcDbContext, CancellationToken, Task> test)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"mhc-history-{Guid.NewGuid():N}.db");
        try
        {
            DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using MhcDbContext context = new(options);
            await context.Database.MigrateAsync(cancellationToken);
            await test(context, cancellationToken);
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

    private static InvoiceEntity CreateInvoice() => new()
    {
        Id = Guid.CreateVersion7(),
        IssuanceYear = 2026,
        Sequence = 100,
        PublicNumber = "MHC-2026-100",
        DocumentType = InvoiceDocumentType.TaxInvoice,
        SourceCustomerId = null,
        BusinessDate = "2026-07-23",
        IssuedAtUtcMs = 1_784_764_801_000,
        IssuedAtSaudiLocal = "2026-07-23T03:00:01.000+03:00",
        IssuedSaudiOffsetMinutes = 180,
        SellerNameArabic = "البائع",
        SellerNameEnglish = "Seller",
        SellerVatNumber = "310123456789003",
        SellerCommercialRegistration = "1234567890",
        SellerBranch = "Main",
        SellerAddress = "Riyadh",
        SellerLogoBytes = [1, 2],
        SellerLogoMimeType = "image/png",
        OperatorName = "Issuer",
        CustomerNameArabic = "العميل",
        CustomerNameEnglish = "Customer",
        CustomerSearchName = ArabicSearchNormalizer.Normalize("العميل Customer"),
        CustomerVatNumber = "310987654321003",
        CustomerCommercialRegistration = "9876543210",
        CustomerAddress = "Jeddah",
        PaymentMethod = PaymentMethod.Card,
        Title = "Original title",
        Notes = "Original notes",
        ShowNotes = true,
        Currency = "SAR",
        SubtotalHalalah = 2_000,
        VatHalalah = 300,
        GrandTotalHalalah = 2_300,
    };

    private static InvoiceLineEntity CreateLine(Guid invoiceId, int position, string description) => new()
    {
        Id = Guid.CreateVersion7(),
        InvoiceId = invoiceId,
        Position = position,
        Description = description,
        Sku = $"SKU-{position}",
        Unit = "unit",
        QuantityMilliunits = 2_000,
        UnitPriceHalalah = 1_000,
        VatCategory = VatCategory.Standard15,
        NetHalalah = 2_000,
        VatHalalah = 300,
        GrossHalalah = 2_300,
    };
}

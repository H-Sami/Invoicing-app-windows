using System.Security.Cryptography;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.Search;
using MHC.Invoicing.Domain.ValueObjects;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using MHC.Invoicing.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Tests.Repositories;

public sealed class InvoiceRepositoryTests
{
    [Fact]
    public async Task SearchAsync_MatchesOnlyExactUuidOrDocumentSerial()
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
            InvoiceEntity target = CreateInvoice("MHC-2026-100", "Target", 100, 1_784_764_801_000);
            InvoiceEntity serialPrefixCollision = CreateInvoice(
                "MHC-2026-1000", "Other", 1000, 1_784_764_802_000);
            context.Invoices.AddRange(target, serialPrefixCollision);
            AddIssuanceAudits(context, target, serialPrefixCollision);
            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({target.Id}, {target.IssuedAtUtcMs});",
                cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({serialPrefixCollision.Id}, {serialPrefixCollision.IssuedAtUtcMs});",
                cancellationToken);
            context.ChangeTracker.Clear();
            InvoiceRepository repository = new(context);

            IReadOnlyList<InvoiceSummary> uuidResults = await repository.SearchAsync(
                target.Id.ToString(), null, null, 20, cancellationToken);
            IReadOnlyList<InvoiceSummary> serialResults = await repository.SearchAsync(
                target.PublicNumber, null, null, 20, cancellationToken);

            Assert.Equal([target.Id], uuidResults.Select(result => result.Id));
            Assert.Equal([target.Id], serialResults.Select(result => result.Id));
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

    [Fact]
    public async Task GetAndSearch_ReturnImmutableHistorySummariesInDeterministicOrder()
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
            InvoiceEntity older = CreateInvoice("MHC-2026-100", "شركة آفاق", 100, 1_784_764_801_000);
            InvoiceEntity newer = CreateInvoice("MHC-2026-101", "آفاق المباشرة", 101, 1_784_764_802_000);
            context.Invoices.AddRange(older, newer);
            AddIssuanceAudits(context, older, newer);
            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({older.Id}, {older.IssuedAtUtcMs});",
                cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({newer.Id}, {newer.IssuedAtUtcMs});",
                cancellationToken);
            context.ChangeTracker.Clear();
            InvoiceRepository repository = new(context);

            InvoiceSummary? loaded = await repository.GetSummaryAsync(older.Id, cancellationToken);
            IReadOnlyList<InvoiceSummary> results = await repository.SearchAsync(
                "افاق",
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 7, 31),
                20,
                cancellationToken);

            Assert.NotNull(loaded);
            Assert.Equal("MHC-2026-100", loaded.PublicNumber);
            Assert.Equal(new Money(1_150), loaded.GrandTotal);
            Assert.Equal([newer.Id, older.Id], results.Select(result => result.Id));
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

    private static InvoiceEntity CreateInvoice(
        string publicNumber,
        string customerName,
        int sequence,
        long issuedAtUtcMs)
    {
        InvoiceEntity invoice = new()
        {
            Id = Guid.CreateVersion7(),
            IssuanceYear = 2026,
            Sequence = sequence,
            PublicNumber = publicNumber,
            DocumentType = InvoiceDocumentType.TaxInvoice,
            BusinessDate = "2026-07-23",
            IssuedAtUtcMs = issuedAtUtcMs,
            IssuedAtSaudiLocal = DateTimeOffset.FromUnixTimeMilliseconds(issuedAtUtcMs)
                .ToOffset(TimeSpan.FromHours(3))
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture),
            IssuedSaudiOffsetMinutes = 180,
            SellerNameArabic = "تقنية MHC",
            SellerVatNumber = "310123456789003",
            SellerBranch = "الرئيسي",
            SellerAddress = "الرياض",
            OperatorName = "المشغل",
            CustomerNameArabic = customerName,
            CustomerSearchName = ArabicSearchNormalizer.Normalize(customerName),
            PaymentMethod = PaymentMethod.Cash,
            Currency = Money.Currency,
            SubtotalHalalah = 1_000,
            VatHalalah = 150,
            GrandTotalHalalah = 1_150,
        };
        invoice.Lines.Add(new InvoiceLineEntity
        {
            Id = Guid.CreateVersion7(),
            InvoiceId = invoice.Id,
            Position = 0,
            Description = "Service",
            Unit = "unit",
            QuantityMilliunits = 1_000,
            UnitPriceHalalah = 1_000,
            VatCategory = VatCategory.Standard15,
            NetHalalah = 1_000,
            VatHalalah = 150,
            GrossHalalah = 1_150,
        });
        byte[] pdf = "%PDF-1.7 history"u8.ToArray();
        invoice.Document = new InvoiceDocumentEntity
        {
            InvoiceId = invoice.Id,
            PdfBytes = pdf,
            Sha256 = SHA256.HashData(pdf),
            ByteLength = pdf.Length,
            MimeType = "application/pdf",
            CreatedAtUtcMs = issuedAtUtcMs,
        };
        return invoice;
    }

    private static void AddIssuanceAudits(MhcDbContext context, params InvoiceEntity[] invoices)
    {
        context.AuditEvents.AddRange(invoices.Select(invoice => new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            InvoiceId = invoice.Id,
            EventType = 1,
            OccurredAtUtcMs = invoice.IssuedAtUtcMs,
            OperatorName = invoice.OperatorName,
        }));
    }
}

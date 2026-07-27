#pragma warning disable xUnit1051
using MHC.Invoicing.Application.Abstractions;
using MHC.Invoicing.Application.Documents;
using MHC.Invoicing.Application.Drafts;
using MHC.Invoicing.Application.Issuance;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;
using MHC.Invoicing.Infrastructure.Documents;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using MHC.Invoicing.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Tests.Persistence;

public sealed class InvoiceIssuanceServiceTests
{
    [Fact]
    public async Task IssueSaleAsync_UsesPersistedDraftAndCompanyProfileAndDeletesExactRevision()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        DateTimeOffset now = new(2026, 12, 31, 22, 30, 0, TimeSpan.Zero);
        DraftRecord persisted = CreateSaleDraft(now, customerName: "العميل الموثوق");
        await database.SaveDraftAsync(persisted);
        await database.SaveCompanyAsync(name: "البائع الموثوق", operatorName: "المشغل الموثوق");

        IssuedInvoice issued = await CreateService(database.ConnectionString, now)
            .IssueSaleAsync(new IssueSaleRequest(persisted.Id, 0));

        Assert.Equal(2027, issued.Number.Year);
        Assert.Equal(100, issued.Number.Sequence);
        Assert.Equal("البائع الموثوق", issued.Seller.NameArabic);
        Assert.Equal("العميل الموثوق", issued.Customer.NameArabic);
        Assert.Equal("المشغل الموثوق", issued.OperatorName);
        await using MhcDbContext verification = database.CreateContext();
        Assert.False(await verification.InvoiceDrafts.AnyAsync());
        Assert.Equal("البائع الموثوق", (await verification.Invoices.SingleAsync()).SellerNameArabic);
    }

    [Fact]
    public async Task IssueSaleAsync_RejectsMissingOrStalePersistedDraftWithoutConsumingNumber()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        DateTimeOffset now = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);
        await database.SaveCompanyAsync();
        DraftRecord draft = CreateSaleDraft(now);
        await database.SaveDraftAsync(draft);

        InvoiceIssuanceService service = CreateService(database.ConnectionString, now);
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(() =>
            service.IssueSaleAsync(new IssueSaleRequest(Guid.CreateVersion7(), 0)));
        await Assert.ThrowsAsync<PersistenceConcurrencyException>(() =>
            service.IssueSaleAsync(new IssueSaleRequest(draft.Id, 1)));

        IssuedInvoice issued = await service.IssueSaleAsync(new IssueSaleRequest(draft.Id, 0));
        Assert.Equal(100, issued.Number.Sequence);
    }

    [Fact]
    public async Task IssueSaleAsync_RejectsMissingCompanyProfileWithoutConsumingNumber()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        DateTimeOffset now = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);
        DraftRecord draft = CreateSaleDraft(now);
        await database.SaveDraftAsync(draft);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            CreateService(database.ConnectionString, now)
                .IssueSaleAsync(new IssueSaleRequest(draft.Id, 0)));

        await using MhcDbContext verification = database.CreateContext();
        Assert.False(await verification.InvoiceSequences.AnyAsync());
        Assert.True(await verification.InvoiceDrafts.AnyAsync(row => row.Id == draft.Id));
    }

    [Fact]
    public async Task IssueSaleAsync_DelayedRenderingDoesNotBlockWritesAndChangedSettingsAreRerendered()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        DateTimeOffset now = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);
        DraftRecord draft = CreateSaleDraft(now);
        await database.SaveDraftAsync(draft);
        await database.SaveCompanyAsync(name: "البائع القديم", revision: 0);
        BlockingPdfRenderer renderer = new();
        Task<IssuedInvoice> issuing = CreateService(database.ConnectionString, now, pdfRenderer: renderer)
            .IssueSaleAsync(new IssueSaleRequest(draft.Id, 0));
        await renderer.FirstRenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using (MhcDbContext writer = database.CreateContext())
        {
            CompanyProfileEntity company = await writer.CompanyProfiles.SingleAsync();
            company.NameArabic = "البائع الجديد";
            company.Revision = 1;
            company.UpdatedAtUtcMs++;
            writer.AppSettings.Add(new AppSettingEntity { Key = "unrelated", Value = "written", UpdatedAtUtcMs = 1 });
            await writer.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        renderer.ReleaseFirstRender.SetResult();

        IssuedInvoice issued = await issuing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("البائع الجديد", issued.Seller.NameArabic);
        Assert.True(renderer.RenderCount >= 2);
        await using MhcDbContext verification = database.CreateContext();
        Assert.True(await verification.AppSettings.AnyAsync(setting => setting.Key == "unrelated"));
    }

    [Fact]
    public async Task IssueSaleAsync_SequenceChangeDuringRenderingRetriesWithGapFreeNumber()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        DateTimeOffset now = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);
        await database.SaveCompanyAsync();
        DraftRecord delayedDraft = CreateSaleDraft(now);
        DraftRecord competingDraft = CreateSaleDraft(now.AddSeconds(1));
        await database.SaveDraftAsync(delayedDraft);
        await database.SaveDraftAsync(competingDraft);
        BlockingPdfRenderer renderer = new();
        Task<IssuedInvoice> delayed = CreateService(database.ConnectionString, now, pdfRenderer: renderer)
            .IssueSaleAsync(new IssueSaleRequest(delayedDraft.Id, 0));
        await renderer.FirstRenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        IssuedInvoice first = await CreateService(database.ConnectionString, now.AddMinutes(1))
            .IssueSaleAsync(new IssueSaleRequest(competingDraft.Id, 0));
        renderer.ReleaseFirstRender.SetResult();
        IssuedInvoice second = await delayed;

        Assert.Equal(100, first.Number.Sequence);
        Assert.Equal(101, second.Number.Sequence);
        Assert.True(renderer.RenderCount >= 2);
    }

    [Fact]
    public async Task IssueSaleAsync_SaudiYearChangesDuringRendering_UsesPostRenderTimingAndNewYearSequence()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        DateTimeOffset beforeSaudiNewYear = new(2026, 12, 31, 20, 59, 59, TimeSpan.Zero);
        DateTimeOffset afterSaudiNewYear = new(2026, 12, 31, 21, 0, 1, TimeSpan.Zero);
        DraftRecord draft = CreateSaleDraft(beforeSaudiNewYear);
        await database.SaveDraftAsync(draft);
        await database.SaveCompanyAsync();
        MutableClock clock = new(beforeSaudiNewYear);
        AdvancingPdfRenderer renderer = new(clock, afterSaudiNewYear);
        InvoiceIssuanceService service = new(
            database.ConnectionString,
            clock,
            new DocumentSerialGenerator(),
            new InvoiceHtmlRenderer(),
            renderer,
            new ZatcaQrGenerator());

        IssuedInvoice issued = await service.IssueSaleAsync(new IssueSaleRequest(draft.Id, 0));

        Assert.Equal(2027, issued.Number.Year);
        Assert.Equal(100, issued.Number.Sequence);
        Assert.Equal(afterSaudiNewYear, issued.Timing.IssuedAtUtc);
        Assert.True(renderer.RenderCount >= 2);
        await using MhcDbContext verification = database.CreateContext();
        InvoiceEntity persisted = await verification.Invoices.SingleAsync();
        Assert.Equal(2027, persisted.IssuanceYear);
        Assert.Equal(afterSaudiNewYear.ToUnixTimeMilliseconds(), persisted.IssuedAtUtcMs);
    }

    [Fact]
    public async Task IssueSaleAsync_PdfFailureLeavesDraftAndNumberAvailable()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        DateTimeOffset now = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);
        await database.SaveCompanyAsync();
        DraftRecord failingDraft = CreateSaleDraft(now);
        await database.SaveDraftAsync(failingDraft);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(database.ConnectionString, now, pdfRenderer: new ThrowingPdfRenderer())
                .IssueSaleAsync(new IssueSaleRequest(failingDraft.Id, 0)));

        await using (MhcDbContext verification = database.CreateContext())
        {
            Assert.True(await verification.InvoiceDrafts.AnyAsync(d => d.Id == failingDraft.Id));
            Assert.False(await verification.Invoices.AnyAsync());
            Assert.False(await verification.InvoiceSequences.AnyAsync());
        }
    }

    [Fact]
    public async Task IssueCreditNoteAsync_RequiresPersistedRevisionAndAtomicallyRevalidatesCreditState()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        DateTimeOffset now = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);
        await database.SaveCompanyAsync(operatorName: "موظف الشركة");
        DraftRecord saleDraft = CreateSaleDraft(now);
        await database.SaveDraftAsync(saleDraft);
        IssuedInvoice sale = await CreateService(database.ConnectionString, now)
            .IssueSaleAsync(new IssueSaleRequest(saleDraft.Id, 0));

        DraftRecord firstCreditDraft = CreateCreditDraft(now, sale.Id, sale.Lines[0].Id, 1.5m);
        await database.SaveDraftAsync(firstCreditDraft);
        IssuedInvoice firstCredit = await CreateService(database.ConnectionString, now.AddMinutes(1))
            .IssueCreditNoteAsync(new IssueCreditNoteRequest(firstCreditDraft.Id, 0));
        Assert.Equal(101, firstCredit.Number.Sequence);
        Assert.Equal("موظف الشركة", firstCredit.OperatorName);

        DraftRecord creditA = CreateCreditDraft(now, sale.Id, sale.Lines[0].Id, 0.5m);
        DraftRecord creditB = CreateCreditDraft(now, sale.Id, sale.Lines[0].Id, 0.5m);
        await database.SaveDraftAsync(creditA);
        await database.SaveDraftAsync(creditB);
        Task<IssuedInvoice> taskA = CreateService(database.ConnectionString, now.AddMinutes(2))
            .IssueCreditNoteAsync(new IssueCreditNoteRequest(creditA.Id, 0));
        Task<IssuedInvoice> taskB = CreateService(database.ConnectionString, now.AddMinutes(2))
            .IssueCreditNoteAsync(new IssueCreditNoteRequest(creditB.Id, 0));
        try { await Task.WhenAll(taskA, taskB); } catch (DomainValidationException) { }

        Task<IssuedInvoice>[] competing = [taskA, taskB];
        Task<IssuedInvoice> completed = Assert.Single(competing, task => task.Status == TaskStatus.RanToCompletion);
        Task<IssuedInvoice> rejected = Assert.Single(competing, task => task.IsFaulted);
        Assert.IsType<DomainValidationException>(rejected.Exception!.InnerException);
        IssuedInvoice successful = await completed;
        Assert.Equal(102, successful.Number.Sequence);
    }

    [Fact]
    public async Task IssueCreditNoteAsync_RejectsOriginalVoidedAfterDraftCreationWithoutConsumingNumber()
    {
        await using TestDatabase database = await TestDatabase.CreateAsync();
        DateTimeOffset now = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);
        await database.SaveCompanyAsync();
        DraftRecord saleDraft = CreateSaleDraft(now);
        await database.SaveDraftAsync(saleDraft);
        IssuedInvoice sale = await CreateService(database.ConnectionString, now)
            .IssueSaleAsync(new IssueSaleRequest(saleDraft.Id, 0));
        DraftRecord creditDraft = CreateCreditDraft(now, sale.Id, sale.Lines[0].Id, 1m);
        await database.SaveDraftAsync(creditDraft);
        await using (MhcDbContext voidContext = database.CreateContext())
        {
            await new InvoiceRepository(voidContext).VoidAsync(
                sale.Id,
                "ألغي الأصل بعد إنشاء المسودة",
                "المشغل",
                now.AddMinutes(1));
        }

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            CreateService(database.ConnectionString, now.AddMinutes(2))
                .IssueCreditNoteAsync(new IssueCreditNoteRequest(creditDraft.Id, 0)));

        await using MhcDbContext verification = database.CreateContext();
        Assert.Single(await verification.Invoices.ToListAsync());
        Assert.True(await verification.InvoiceDrafts.AnyAsync(draft => draft.Id == creditDraft.Id));
        Assert.Equal(101, (await verification.InvoiceSequences.SingleAsync()).NextValue);
    }

    private static InvoiceIssuanceService CreateService(
        string connectionString,
        DateTimeOffset now,
        IInvoicePdfRenderer? pdfRenderer = null) => new(
            connectionString,
            new FixedClock(now),
            new DocumentSerialGenerator(),
            new InvoiceHtmlRenderer(),
            pdfRenderer ?? new FixedPdfRenderer(),
            new ZatcaQrGenerator());

    private static DraftRecord CreateSaleDraft(DateTimeOffset createdAt, string customerName = "عميل نقدي") => new(
        Guid.CreateVersion7(), InvoiceDocumentType.TaxInvoice, null, null,
        new DateOnly(2026, 7, 23), new DraftParty(customerName, null, null, null, "الرياض"),
        PaymentMethod.Cash, "فاتورة خدمات", "شكراً", true,
        [new InvoiceDraftLine(Guid.CreateVersion7(), null, "خدمة", "SVC", "hour", 2m,
            Money.FromRiyals(100m), VatCategory.Standard15, null, null)], createdAt, createdAt);

    private static DraftRecord CreateCreditDraft(
        DateTimeOffset createdAt, Guid originalId, Guid originalLineId, decimal quantity) => new(
        Guid.CreateVersion7(), InvoiceDocumentType.CreditNote, originalId, null,
        new DateOnly(2026, 7, 23), new DraftParty("ignored persisted customer", null, null, null, null),
        PaymentMethod.BankTransfer, "إشعار دائن", "تصحيح", true,
        [new InvoiceDraftLine(Guid.CreateVersion7(), null, "ignored", null, "unit", quantity,
            Money.Zero, VatCategory.Standard15, null, null, originalLineId)], createdAt, createdAt);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _path;
        private TestDatabase(string path)
        {
            _path = path;
            ConnectionString = $"Data Source={path};Default Timeout=10;Foreign Keys=True";
        }
        public string ConnectionString { get; }
        public static async Task<TestDatabase> CreateAsync()
        {
            TestDatabase database = new(Path.Combine(Path.GetTempPath(), $"issuance-{Guid.NewGuid():N}.db"));
            await using MhcDbContext context = database.CreateContext();
            await context.Database.MigrateAsync();
            return database;
        }
        public MhcDbContext CreateContext() => new(new DbContextOptionsBuilder<MhcDbContext>().UseSqlite(ConnectionString).Options);
        public async Task SaveDraftAsync(DraftRecord draft)
        {
            await using MhcDbContext context = CreateContext();
            await new DraftRepository(context).SaveAsync(draft, null);
        }
        public async Task SaveCompanyAsync(string name = "مؤسسة إم إتش سي", string operatorName = "المشغل", int revision = 0)
        {
            await using MhcDbContext context = CreateContext();
            context.CompanyProfiles.Add(new CompanyProfileEntity
            {
                Id = 1,
                Revision = revision,
                NameArabic = name,
                NameEnglish = "MHC",
                VatNumber = "310123456700003",
                CommercialRegistration = "1234567890",
                Branch = "الرئيسي",
                Address = "الرياض",
                OperatorName = operatorName,
                DefaultPaymentMethod = PaymentMethod.Cash,
                CreatedAtUtcMs = 1,
                UpdatedAtUtcMs = 1,
            });
            await context.SaveChangesAsync();
        }
        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" }) if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
        public DateTimeOffset SaudiNow => MHC.Invoicing.Domain.Time.SaudiTime.ToLocal(utcNow);
    }
    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public DateTimeOffset SaudiNow => MHC.Invoicing.Domain.Time.SaudiTime.ToLocal(UtcNow);
    }
    private sealed class FixedPdfRenderer : IInvoicePdfRenderer
    {
        public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default) => Task.FromResult("%PDF-1.4\n%%EOF"u8.ToArray());
    }
    private sealed class ThrowingPdfRenderer : IInvoicePdfRenderer
    {
        public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default) => throw new InvalidOperationException("PDF rendering failed.");
    }
    private sealed class BlockingPdfRenderer : IInvoicePdfRenderer
    {
        private int _count;
        public int RenderCount => Volatile.Read(ref _count);
        public TaskCompletionSource FirstRenderStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRender { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _count) == 1)
            {
                FirstRenderStarted.SetResult();
                await ReleaseFirstRender.Task.WaitAsync(cancellationToken);
            }
            return "%PDF-1.4\n%%EOF"u8.ToArray();
        }
    }
    private sealed class AdvancingPdfRenderer(MutableClock clock, DateTimeOffset advancedUtc) : IInvoicePdfRenderer
    {
        private int _count;
        public int RenderCount => Volatile.Read(ref _count);
        public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _count) == 1)
            {
                clock.UtcNow = advancedUtc;
            }
            return Task.FromResult("%PDF-1.4\n%%EOF"u8.ToArray());
        }
    }
}

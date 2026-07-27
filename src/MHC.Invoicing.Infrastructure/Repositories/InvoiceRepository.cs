using System.Globalization;
using System.Security.Cryptography;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.Search;
using MHC.Invoicing.Domain.ValueObjects;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Repositories;

public sealed class InvoiceRepository(MhcDbContext context) : IInvoiceRepository
{
    public async Task<InvoiceSummary?> GetSummaryAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Guid> finalizedIds = FinalizedInvoiceIds();
        InvoiceEntity? entity = await context.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Void)
            .SingleOrDefaultAsync(
                invoice => invoice.Id == id && finalizedIds.Contains(invoice.Id),
                cancellationToken);
        return entity is null ? null : ToSummary(entity);
    }

    public async Task<IReadOnlyList<InvoiceSummary>> SearchAsync(
        string? searchText,
        DateOnly? fromBusinessDate,
        DateOnly? toBusinessDate,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 200);
        if (fromBusinessDate > toBusinessDate)
        {
            throw new ArgumentException("The start date cannot follow the end date.", nameof(fromBusinessDate));
        }

        string rawSearch = searchText?.Trim() ?? string.Empty;
        string normalizedSearch = ArabicSearchNormalizer.Normalize(rawSearch);
        Guid? exactInvoiceId = Guid.TryParse(rawSearch, out Guid parsedInvoiceId) ? parsedInvoiceId : null;
        string? fromDate = fromBusinessDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string? toDate = toBusinessDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        IQueryable<Guid> finalizedIds = FinalizedInvoiceIds();
        IQueryable<InvoiceEntity> query = context.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Void)
            .Where(invoice => finalizedIds.Contains(invoice.Id));

        if (normalizedSearch.Length > 0)
        {
            query = query.Where(invoice =>
                (exactInvoiceId != null && invoice.Id == exactInvoiceId.Value) ||
                invoice.PublicNumber == rawSearch ||
                invoice.CustomerSearchName.Contains(normalizedSearch) ||
                (invoice.CustomerVatNumber != null && invoice.CustomerVatNumber.Contains(rawSearch)) ||
                (invoice.CustomerCommercialRegistration != null &&
                    invoice.CustomerCommercialRegistration.Contains(rawSearch)));
        }

        if (fromDate is not null)
        {
#pragma warning disable CA1309 // EF Core translates only the two-argument overload to SQL.
            query = query.Where(invoice => string.Compare(invoice.BusinessDate, fromDate) >= 0);
#pragma warning restore CA1309
        }

        if (toDate is not null)
        {
#pragma warning disable CA1309 // EF Core translates only the two-argument overload to SQL.
            query = query.Where(invoice => string.Compare(invoice.BusinessDate, toDate) <= 0);
#pragma warning restore CA1309
        }

        List<InvoiceEntity> entities = await query
            .OrderBy(invoice =>
                normalizedSearch.Length == 0 ||
                invoice.PublicNumber.StartsWith(rawSearch) ||
                invoice.CustomerSearchName.StartsWith(normalizedSearch)
                    ? 0
                    : 1)
            .ThenByDescending(invoice => invoice.IssuedAtUtcMs)
            .ThenBy(invoice => invoice.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return entities.ConvertAll(ToSummary).AsReadOnly();
    }

    public async Task<InvoiceSnapshot?> GetSnapshotAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Guid> finalizedIds = FinalizedInvoiceIds();
        InvoiceEntity? entity = await context.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Void)
            .SingleOrDefaultAsync(
                invoice => invoice.Id == id && finalizedIds.Contains(invoice.Id),
                cancellationToken);
        if (entity is null)
            return null;

        string? originalInvoicePublicNumber = entity.OriginalInvoiceId is Guid originalInvoiceId
            ? await context.Invoices.AsNoTracking()
                .Where(invoice => invoice.Id == originalInvoiceId)
                .Select(invoice => invoice.PublicNumber)
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        return ToSnapshot(entity, originalInvoicePublicNumber);
    }

    public async Task<InvoiceDocument?> GetDocumentAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Guid> finalizedIds = FinalizedInvoiceIds();
        InvoiceDocumentEntity? entity = await context.InvoiceDocuments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                document => document.InvoiceId == id && finalizedIds.Contains(document.InvoiceId),
                cancellationToken);
        if (entity is null)
        {
            return null;
        }

        byte[] actualHash = SHA256.HashData(entity.PdfBytes);
        if (entity.ByteLength != entity.PdfBytes.LongLength ||
            entity.Sha256.Length != actualHash.Length ||
            !CryptographicOperations.FixedTimeEquals(entity.Sha256, actualHash))
        {
            throw new InvalidDataException($"Stored PDF for invoice {id} failed its integrity check.");
        }

        return new InvoiceDocument(
            entity.PdfBytes,
            entity.MimeType,
            DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUtcMs));
    }

    public async Task<InvoiceVoidInfo> VoidAsync(
        Guid id,
        string reason,
        string operatorName,
        DateTimeOffset voidedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (voidedAtUtc == default || voidedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Void timestamp must be a non-default UTC value.", nameof(voidedAtUtc));
        }

        IQueryable<Guid> finalizedIds = FinalizedInvoiceIds();
        bool exists = await context.Invoices.AsNoTracking()
            .AnyAsync(invoice => invoice.Id == id && finalizedIds.Contains(invoice.Id), cancellationToken);
        if (!exists)
        {
            throw new InvoiceNotFoundException(id);
        }

        if (await context.InvoiceVoids.AsNoTracking().AnyAsync(invoiceVoid => invoiceVoid.InvoiceId == id, cancellationToken))
        {
            throw new InvoiceAlreadyVoidedException(id);
        }

        long timestamp = voidedAtUtc.ToUnixTimeMilliseconds();
        int inserted = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO invoice_voids (invoice_id, reason, voided_at_utc_ms, operator_name)
            SELECT {id}, {reason}, {timestamp}, {operatorName}
            WHERE EXISTS (
                SELECT 1
                FROM invoices AS i
                JOIN invoice_finalizations AS f ON f.invoice_id = i.id
                WHERE i.id = {id})
            ON CONFLICT(invoice_id) DO NOTHING
            """,
            cancellationToken);
        if (inserted != 1)
        {
            bool stillExists = await context.Invoices.AsNoTracking()
                .AnyAsync(
                    invoice => invoice.Id == id && finalizedIds.Contains(invoice.Id),
                    cancellationToken);
            throw stillExists ? new InvoiceAlreadyVoidedException(id) : new InvoiceNotFoundException(id);
        }

        return new InvoiceVoidInfo(reason, voidedAtUtc, operatorName);
    }

    private IQueryable<Guid> FinalizedInvoiceIds() =>
        context.Database.SqlQueryRaw<Guid>(
            "SELECT invoice_id AS Value FROM invoice_finalizations");

    private static InvoiceSummary ToSummary(InvoiceEntity entity) => new(
        entity.Id,
        entity.PublicNumber,
        entity.DocumentType,
        DateOnly.ParseExact(entity.BusinessDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset.FromUnixTimeMilliseconds(entity.IssuedAtUtcMs),
        entity.CustomerNameArabic,
        entity.CustomerNameEnglish,
        new Money(entity.GrandTotalHalalah),
        entity.Void is not null);

    private static InvoiceSnapshot ToSnapshot(InvoiceEntity entity, string? originalInvoicePublicNumber) => new(
        entity.Id,
        entity.IssuanceYear,
        entity.Sequence,
        entity.PublicNumber,
        entity.DocumentType,
        entity.OriginalInvoiceId,
        originalInvoicePublicNumber,
        entity.SourceCustomerId,
        DateOnly.ParseExact(entity.BusinessDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset.FromUnixTimeMilliseconds(entity.IssuedAtUtcMs),
        DateTimeOffset.Parse(entity.IssuedAtSaudiLocal, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        PartySnapshot.Create(
            entity.SellerNameArabic,
            entity.SellerNameEnglish,
            entity.SellerVatNumber,
            entity.SellerCommercialRegistration,
            entity.SellerAddress),
        entity.SellerBranch,
        entity.SellerLogoBytes?.ToArray(),
        entity.SellerLogoMimeType,
        entity.OperatorName,
        PartySnapshot.Create(
            entity.CustomerNameArabic,
            entity.CustomerNameEnglish,
            entity.CustomerVatNumber,
            entity.CustomerCommercialRegistration,
            entity.CustomerAddress),
        entity.PaymentMethod,
        entity.Title,
        entity.Notes,
        entity.ShowNotes,
        entity.Currency,
        new Money(entity.SubtotalHalalah),
        new Money(entity.VatHalalah),
        new Money(entity.GrandTotalHalalah),
        Array.AsReadOnly(entity.Lines
            .OrderBy(line => line.Position)
            .Select(ToLineSnapshot)
            .ToArray()),
        entity.Void is null
            ? null
            : new InvoiceVoidInfo(
                entity.Void.Reason,
                DateTimeOffset.FromUnixTimeMilliseconds(entity.Void.VoidedAtUtcMs),
                entity.Void.OperatorName));

    private static InvoiceLineSnapshot ToLineSnapshot(InvoiceLineEntity line) => new(
        line.Id,
        line.SourceCatalogItemId,
        line.OriginalInvoiceLineId,
        line.Description,
        line.Sku,
        line.Unit,
        line.QuantityMilliunits / 1_000m,
        new Money(line.UnitPriceHalalah),
        line.VatCategory,
        line.TaxExemptionReasonCode,
        line.TaxExemptionReason,
        new Money(line.NetHalalah),
        new Money(line.VatHalalah),
        new Money(line.GrossHalalah));
}

using System.Globalization;
using MHC.Invoicing.Application.Drafts;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Application.Workflows;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Repositories;

public sealed class DraftRepository(MhcDbContext context) : IDraftRepository, IResumeDraftSource
{
    public async Task<IReadOnlyList<ResumableDraft>> LoadAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return await context.InvoiceDrafts
            .AsNoTracking()
            .OrderByDescending(draft => draft.UpdatedAtUtcMs)
            .ThenByDescending(draft => draft.Id)
            .Take(limit)
            .Select(draft => new ResumableDraft(
                draft.Id,
                draft.DocumentType,
                DateOnly.ParseExact(draft.BusinessDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                draft.CustomerNameArabic,
                draft.Lines.Count,
                DateTimeOffset.FromUnixTimeMilliseconds(draft.UpdatedAtUtcMs)))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<VersionedDraft> SaveAsync(
        DraftRecord draft,
        int? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);

        InvoiceDraftEntity entity;
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = expectedRevision is null
            ? null
            : await context.Database.BeginTransactionAsync(cancellationToken);
        if (expectedRevision is null)
        {
            entity = ToEntity(draft, revision: 0);
            context.InvoiceDrafts.Add(entity);
        }
        else
        {
            ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision.Value);
            entity = ToEntity(draft, checked(expectedRevision.Value + 1));
            List<InvoiceDraftLineEntity> replacementLines = entity.Lines.ToList();
            entity.Lines.Clear();
            context.Attach(entity);
            context.Entry(entity).State = EntityState.Modified;
            context.Entry(entity).Property(row => row.Revision).OriginalValue = expectedRevision.Value;
            await context.InvoiceDraftLines
                .Where(line => line.DraftId == draft.Id)
                .ExecuteDeleteAsync(cancellationToken);
            context.InvoiceDraftLines.AddRange(replacementLines);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new PersistenceConcurrencyException(
                $"Draft {draft.Id} was modified or deleted by another operation.",
                exception);
        }
        finally
        {
            Detach(entity);
        }

        return new VersionedDraft(draft, entity.Revision);
    }

    public async Task<VersionedDraft?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        InvoiceDraftEntity? entity = await context.InvoiceDrafts
            .AsNoTracking()
            .Include(draft => draft.Lines)
            .SingleOrDefaultAsync(draft => draft.Id == id, cancellationToken);
        return entity is null ? null : ToVersionedDraft(entity);
    }

    public async Task DeleteAsync(
        Guid id,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        int deleted = await context.InvoiceDrafts
            .Where(draft => draft.Id == id && draft.Revision == expectedRevision)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted != 1)
        {
            throw ConcurrencyFailure(id, expectedRevision);
        }
    }

    private static void ValidateDraft(DraftRecord draft)
    {
        if (draft.Id == Guid.Empty)
        {
            throw new ArgumentException("Draft ID cannot be empty.", nameof(draft));
        }

        if (draft.CreatedAtUtc.Offset != TimeSpan.Zero || draft.UpdatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Draft timestamps must be UTC.", nameof(draft));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(draft.UpdatedAtUtc, draft.CreatedAtUtc);
        if (draft.Lines.Select(line => line.Id).Distinct().Count() != draft.Lines.Count)
        {
            throw new ArgumentException("Draft line IDs must be unique.", nameof(draft));
        }
    }

    private static InvoiceDraftEntity ToEntity(DraftRecord draft, int revision)
    {
        InvoiceDraftEntity entity = new();
        Apply(draft, entity, revision);
        return entity;
    }

    private static void Apply(DraftRecord draft, InvoiceDraftEntity entity, int revision)
    {
        entity.Id = draft.Id;
        entity.Revision = revision;
        entity.DocumentType = draft.DocumentType;
        entity.OriginalInvoiceId = draft.OriginalInvoiceId;
        entity.CustomerId = draft.CustomerId;
        entity.BusinessDate = draft.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        entity.CustomerNameArabic = draft.Customer.Name;
        entity.CustomerNameEnglish = draft.Customer.NameEnglish;
        entity.CustomerVatNumber = draft.Customer.VatNumber;
        entity.CustomerCommercialRegistration = draft.Customer.CommercialRegistration;
        entity.CustomerAddress = draft.Customer.Address;
        entity.PaymentMethod = draft.PaymentMethod;
        entity.Title = draft.Title;
        entity.Notes = draft.Notes;
        entity.ShowNotes = draft.ShowNotes;
        entity.CreatedAtUtcMs = draft.CreatedAtUtc.ToUnixTimeMilliseconds();
        entity.UpdatedAtUtcMs = draft.UpdatedAtUtc.ToUnixTimeMilliseconds();
        entity.Lines.Clear();
        for (int position = 0; position < draft.Lines.Count; position++)
        {
            entity.Lines.Add(ToEntity(draft.Id, draft.Lines[position], position));
        }
    }

    private static InvoiceDraftLineEntity ToEntity(Guid draftId, InvoiceDraftLine line, int position) => new()
    {
        Id = line.Id,
        DraftId = draftId,
        Position = position,
        CatalogItemId = line.CatalogItemId,
        OriginalInvoiceLineId = line.OriginalInvoiceLineId,
        Description = line.Description,
        Sku = line.Sku,
        Unit = line.Unit,
        QuantityMilliunits = ToMilliunits(line.Quantity),
        UnitPriceHalalah = line.UnitPrice.Halalah,
        VatCategory = line.VatCategory,
        TaxExemptionReasonCode = line.TaxExemptionReasonCode,
        TaxExemptionReason = line.TaxExemptionReason,
    };

    private static long ToMilliunits(decimal quantity)
    {
        decimal scaled = checked(quantity * 1_000m);
        if (scaled != decimal.Truncate(scaled))
        {
            throw new ArgumentException("Draft quantities support at most three decimal places.", nameof(quantity));
        }

        return decimal.ToInt64(scaled);
    }

    private static VersionedDraft ToVersionedDraft(InvoiceDraftEntity entity) => new(
        new DraftRecord(
            entity.Id,
            entity.DocumentType,
            entity.OriginalInvoiceId,
            entity.CustomerId,
            DateOnly.ParseExact(entity.BusinessDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            new DraftParty(
                entity.CustomerNameArabic,
                entity.CustomerNameEnglish,
                entity.CustomerVatNumber,
                entity.CustomerCommercialRegistration,
                entity.CustomerAddress),
            entity.PaymentMethod,
            entity.Title,
            entity.Notes,
            entity.ShowNotes,
            entity.Lines
                .OrderBy(line => line.Position)
                .Select(ToDomainLine)
                .ToArray(),
            DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUtcMs),
            DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUtcMs)),
        entity.Revision);

    private static InvoiceDraftLine ToDomainLine(InvoiceDraftLineEntity line) => new(
        line.Id,
        line.CatalogItemId,
        line.Description,
        line.Sku,
        line.Unit,
        line.QuantityMilliunits / 1_000m,
        new Money(line.UnitPriceHalalah),
        line.VatCategory,
        line.TaxExemptionReasonCode,
        line.TaxExemptionReason,
        line.OriginalInvoiceLineId);

    private static PersistenceConcurrencyException ConcurrencyFailure(Guid id, int expectedRevision) => new(
        $"Draft {id} does not exist at revision {expectedRevision}.",
        new DbUpdateConcurrencyException());

    private void Detach(InvoiceDraftEntity entity)
    {
        foreach (InvoiceDraftLineEntity line in entity.Lines.ToArray())
        {
            context.Entry(line).State = EntityState.Detached;
        }

        context.Entry(entity).State = EntityState.Detached;
    }
}

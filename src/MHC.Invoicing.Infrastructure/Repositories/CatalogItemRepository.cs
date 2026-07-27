using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Catalog;
using MHC.Invoicing.Domain.Search;
using MHC.Invoicing.Domain.ValueObjects;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Repositories;

public sealed class CatalogItemRepository(MhcDbContext context) : ICatalogItemRepository
{
    public async Task<VersionedCatalogItem> AddAsync(
        CatalogItem catalogItem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogItem);
        CatalogItemEntity entity = ToEntity(catalogItem, 0);
        context.CatalogItems.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        context.Entry(entity).State = EntityState.Detached;
        return new VersionedCatalogItem(catalogItem, entity.Revision);
    }

    public async Task<VersionedCatalogItem?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        CatalogItemEntity? entity = await context.CatalogItems
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return entity is null ? null : ToVersionedCatalogItem(entity);
    }

    public async Task<IReadOnlyList<VersionedCatalogItem>> SearchAsync(
        string? searchText,
        bool includeArchived,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);

        string normalizedSearch = ArabicSearchNormalizer.Normalize(searchText?.Trim());
        IQueryable<CatalogItemEntity> query = context.CatalogItems.AsNoTracking();
        if (!includeArchived)
        {
            query = query.Where(item => !item.IsArchived);
        }

        if (normalizedSearch.Length > 0)
        {
            query = query.Where(item =>
                item.SearchNameArabic.Contains(normalizedSearch) ||
                item.SearchNameEnglish.Contains(normalizedSearch) ||
                item.SearchSku.Contains(normalizedSearch));
        }

        List<CatalogItemEntity> entities = await query
            .OrderBy(item =>
                normalizedSearch.Length == 0 ||
                item.SearchNameArabic.StartsWith(normalizedSearch) ||
                item.SearchNameEnglish.StartsWith(normalizedSearch) ||
                item.SearchSku.StartsWith(normalizedSearch)
                    ? 0
                    : 1)
            .ThenBy(item => item.NameArabic)
            .ThenBy(item => item.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return entities.ConvertAll(ToVersionedCatalogItem).AsReadOnly();
    }

    public async Task<VersionedCatalogItem> UpdateAsync(
        CatalogItem catalogItem,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogItem);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);

        CatalogItemEntity entity = ToEntity(catalogItem, checked(expectedRevision + 1));
        context.Attach(entity);
        context.Entry(entity).State = EntityState.Modified;
        context.Entry(entity).Property(row => row.Revision).OriginalValue = expectedRevision;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new PersistenceConcurrencyException(
                $"Catalog item {catalogItem.Id} was modified or deleted by another operation.",
                exception);
        }
        finally
        {
            context.Entry(entity).State = EntityState.Detached;
        }

        return new VersionedCatalogItem(catalogItem, entity.Revision);
    }

    private static CatalogItemEntity ToEntity(CatalogItem catalogItem, int revision) => new()
    {
        Id = catalogItem.Id,
        NameArabic = catalogItem.NameArabic,
        NameEnglish = catalogItem.NameEnglish,
        SearchNameArabic = catalogItem.SearchNameArabic,
        SearchNameEnglish = catalogItem.SearchNameEnglish,
        Sku = catalogItem.Sku,
        SearchSku = catalogItem.SearchSku,
        Unit = catalogItem.Unit.Value,
        DefaultUnitPriceHalalah = catalogItem.DefaultUnitPrice.Halalah,
        VatCategory = catalogItem.VatCategory,
        IsArchived = catalogItem.IsArchived,
        Revision = revision,
        CreatedAtUtcMs = catalogItem.CreatedAtUtc.ToUnixTimeMilliseconds(),
        UpdatedAtUtcMs = catalogItem.UpdatedAtUtc.ToUnixTimeMilliseconds(),
    };

    private static VersionedCatalogItem ToVersionedCatalogItem(CatalogItemEntity entity) => new(
        CatalogItem.Rehydrate(
            entity.Id,
            entity.NameArabic,
            entity.NameEnglish,
            entity.Sku,
            UnitOfMeasure.Create(entity.Unit),
            new Money(entity.DefaultUnitPriceHalalah),
            entity.VatCategory,
            DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUtcMs),
            DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUtcMs),
            entity.IsArchived),
        entity.Revision);
}

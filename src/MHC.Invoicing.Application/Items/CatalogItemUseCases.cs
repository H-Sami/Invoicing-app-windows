using MHC.Invoicing.Application.Abstractions;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Catalog;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Application.Items;

public sealed record CatalogItemCommand(
    string NameArabic,
    string? NameEnglish,
    string? Sku,
    string Unit,
    Money DefaultUnitPrice,
    VatCategory VatCategory);

public sealed record CatalogItemSuggestion(
    Guid Id,
    string NameArabic,
    string? NameEnglish,
    string? Sku,
    string Unit,
    Money DefaultUnitPrice,
    VatCategory VatCategory,
    bool IsArchived);

public sealed class CreateCatalogItem(ICatalogItemRepository repository, IClock clock)
{
    public Task<VersionedCatalogItem> ExecuteAsync(
        CatalogItemCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        CatalogItem item = CatalogItem.Create(
            command.NameArabic,
            command.NameEnglish,
            command.Sku,
            UnitOfMeasure.Create(command.Unit),
            command.DefaultUnitPrice,
            command.VatCategory,
            clock.UtcNow);
        return repository.AddAsync(item, cancellationToken);
    }
}

public sealed class UpdateCatalogItem(ICatalogItemRepository repository, IClock clock)
{
    public async Task<VersionedCatalogItem> ExecuteAsync(
        Guid id,
        int expectedRevision,
        CatalogItemCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        VersionedCatalogItem current = await CatalogItemLookup.GetRequiredAsync(repository, id, cancellationToken);
        if (current.Revision != expectedRevision)
        {
            throw new PersistenceConcurrencyException(
                $"Catalog item {id} has revision {current.Revision}, not {expectedRevision}.",
                new InvalidOperationException("Revision mismatch."));
        }

        current.CatalogItem.Update(
            command.NameArabic,
            command.NameEnglish,
            command.Sku,
            UnitOfMeasure.Create(command.Unit),
            command.DefaultUnitPrice,
            command.VatCategory,
            clock.UtcNow);
        return await repository.UpdateAsync(current.CatalogItem, expectedRevision, cancellationToken);
    }
}

public sealed class ArchiveCatalogItem(ICatalogItemRepository repository, IClock clock)
{
    public async Task<VersionedCatalogItem> ExecuteAsync(
        Guid id,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        VersionedCatalogItem current = await CatalogItemLookup.GetRequiredAsync(repository, id, cancellationToken);
        current.CatalogItem.Archive(clock.UtcNow);
        return await repository.UpdateAsync(current.CatalogItem, expectedRevision, cancellationToken);
    }
}

public sealed class RestoreCatalogItem(ICatalogItemRepository repository, IClock clock)
{
    public async Task<VersionedCatalogItem> ExecuteAsync(
        Guid id,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        VersionedCatalogItem current = await CatalogItemLookup.GetRequiredAsync(repository, id, cancellationToken);
        current.CatalogItem.Restore(clock.UtcNow);
        return await repository.UpdateAsync(current.CatalogItem, expectedRevision, cancellationToken);
    }
}

public sealed class SearchCatalogItems(ICatalogItemRepository repository)
{
    public async Task<IReadOnlyList<CatalogItemSuggestion>> ExecuteAsync(
        string? searchText,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<VersionedCatalogItem> items = await repository.SearchAsync(
            searchText,
            includeArchived: false,
            limit: 20,
            cancellationToken);
        return items.Select(item => ToSuggestion(item.CatalogItem)).ToArray();
    }

    private static CatalogItemSuggestion ToSuggestion(CatalogItem item) => new(
        item.Id,
        item.NameArabic,
        item.NameEnglish,
        item.Sku,
        item.Unit.Value,
        item.DefaultUnitPrice,
        item.VatCategory,
        item.IsArchived);
}

public sealed class SelectCatalogItem(ICatalogItemRepository repository)
{
    public async Task<InvoiceDraftLine> ExecuteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        VersionedCatalogItem current = await CatalogItemLookup.GetRequiredAsync(repository, id, cancellationToken);
        if (current.CatalogItem.IsArchived)
        {
            throw new InvalidOperationException("Archived catalog items cannot be selected.");
        }

        CatalogItem item = current.CatalogItem;
        return new InvoiceDraftLine(
            Guid.CreateVersion7(),
            item.Id,
            item.NameArabic,
            item.Sku,
            item.Unit.Value,
            1m,
            item.DefaultUnitPrice,
            item.VatCategory,
            null,
            null);
    }
}

file static class CatalogItemLookup
{
    public static async Task<VersionedCatalogItem> GetRequiredAsync(
        ICatalogItemRepository repository,
        Guid id,
        CancellationToken cancellationToken)
    {
        VersionedCatalogItem? item = await repository.GetAsync(id, cancellationToken);
        return item ?? throw new KeyNotFoundException($"Catalog item {id} was not found.");
    }
}

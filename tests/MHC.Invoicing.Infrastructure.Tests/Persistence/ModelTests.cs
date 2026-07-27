using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MHC.Invoicing.Infrastructure.Tests.Persistence;

public sealed class ModelTests
{
    [Fact]
    public void Model_MapsEveryRequiredTableWithExplicitNames()
    {
        using MhcDbContext context = CreateContext();

        string[] tables = context.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(name => name is not null)
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "app_settings",
                "audit_events",
                "catalog_items",
                "company_profiles",
                "customers",
                "invoice_documents",
                "invoice_draft_lines",
                "invoice_drafts",
                "invoice_lines",
                "invoice_sequences",
                "invoice_voids",
                "invoices",
            ],
            tables);
    }

    [Fact]
    public void Model_DeclaresRequiredUniqueAndFilteredIndexes()
    {
        using MhcDbContext context = CreateContext();

        AssertUniqueIndex(context, typeof(InvoiceEntity), nameof(InvoiceEntity.PublicNumber));
        AssertUniqueIndex(
            context,
            typeof(InvoiceEntity),
            nameof(InvoiceEntity.IssuanceYear),
            nameof(InvoiceEntity.Sequence));
        AssertUniqueIndex(
            context,
            typeof(InvoiceDraftLineEntity),
            nameof(InvoiceDraftLineEntity.DraftId),
            nameof(InvoiceDraftLineEntity.Position));
        AssertUniqueIndex(
            context,
            typeof(InvoiceLineEntity),
            nameof(InvoiceLineEntity.InvoiceId),
            nameof(InvoiceLineEntity.Position));

        IEntityType catalog = context.Model.FindEntityType(typeof(CatalogItemEntity))!;
        IIndex activeSku = Assert.Single(catalog.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(CatalogItemEntity.SearchSku)]));
        Assert.Contains("is_archived = 0", activeSku.GetFilter(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Model_UsesRestrictiveDeleteBehaviorForIssuedHistory()
    {
        using MhcDbContext context = CreateContext();

        foreach (Type entityType in new[]
                 {
                     typeof(InvoiceEntity),
                     typeof(InvoiceLineEntity),
                     typeof(InvoiceDocumentEntity),
                     typeof(InvoiceVoidEntity),
                 })
        {
            IEntityType entity = context.Model.FindEntityType(entityType)!;
            Assert.All(entity.GetForeignKeys(), foreignKey =>
                Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
        }
    }

    [Fact]
    public void Model_StoresMoneyAndQuantitiesAsIntegersNeverReal()
    {
        using MhcDbContext context = CreateContext();

        AssertColumnType(context, typeof(CatalogItemEntity), nameof(CatalogItemEntity.DefaultUnitPriceHalalah), "INTEGER");
        AssertColumnType(context, typeof(InvoiceDraftLineEntity), nameof(InvoiceDraftLineEntity.QuantityMilliunits), "INTEGER");
        AssertColumnType(context, typeof(InvoiceDraftLineEntity), nameof(InvoiceDraftLineEntity.UnitPriceHalalah), "INTEGER");
        AssertColumnType(context, typeof(InvoiceLineEntity), nameof(InvoiceLineEntity.QuantityMilliunits), "INTEGER");
        AssertColumnType(context, typeof(InvoiceLineEntity), nameof(InvoiceLineEntity.NetHalalah), "INTEGER");
        AssertColumnType(context, typeof(InvoiceEntity), nameof(InvoiceEntity.GrandTotalHalalah), "INTEGER");
    }

    private static MhcDbContext CreateContext()
    {
        DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new MhcDbContext(options);
    }

    private static void AssertUniqueIndex(
        DbContext context,
        Type entityType,
        params string[] propertyNames)
    {
        IEntityType entity = context.Model.FindEntityType(entityType)!;
        Assert.Contains(entity.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
    }

    private static void AssertColumnType(
        DbContext context,
        Type entityType,
        string propertyName,
        string expectedType)
    {
        IEntityType entity = context.Model.FindEntityType(entityType)!;
        IProperty property = entity.FindProperty(propertyName)!;
        StoreObjectIdentifier table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        Assert.Equal(expectedType, property.GetColumnType(table));
    }
}

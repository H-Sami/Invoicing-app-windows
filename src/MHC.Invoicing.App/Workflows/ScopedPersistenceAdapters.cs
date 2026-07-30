using MHC.Invoicing.Application.Maintenance;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Application.Workflows;
using MHC.Invoicing.Domain.Catalog;
using MHC.Invoicing.Domain.Customers;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.App.Workflows;

internal static class ScopedPersistence
{
    internal static MhcDbContext CreateContext(string connectionString)
    {
        DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new MhcDbContext(options);
    }
}

internal sealed class ScopedDraftRepository(
    string connectionString,
    IApplicationWorkGate workGate) : IDraftRepository
{
    public async Task<VersionedDraft> SaveAsync(
        Application.Drafts.DraftRecord draft,
        int? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
        await using MhcDbContext context = ScopedPersistence.CreateContext(connectionString);
        return await new DraftRepository(context).SaveAsync(draft, expectedRevision, cancellationToken);
    }

    public async Task<VersionedDraft?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
        await using MhcDbContext context = ScopedPersistence.CreateContext(connectionString);
        return await new DraftRepository(context).GetAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, int expectedRevision, CancellationToken cancellationToken = default)
    {
        await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
        await using MhcDbContext context = ScopedPersistence.CreateContext(connectionString);
        await new DraftRepository(context).DeleteAsync(id, expectedRevision, cancellationToken);
    }
}

internal sealed class ScopedCustomerRepository(
    string connectionString,
    IApplicationWorkGate workGate) : ICustomerRepository
{
    public Task<VersionedCustomer> AddAsync(Customer customer, CancellationToken cancellationToken = default) =>
        ExecuteAsync(repository => repository.AddAsync(customer, cancellationToken), cancellationToken);

    public Task<VersionedCustomer?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ExecuteAsync(repository => repository.GetAsync(id, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<VersionedCustomer>> SearchAsync(
        string? searchText,
        bool includeArchived,
        int limit,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(repository => repository.SearchAsync(searchText, includeArchived, limit, cancellationToken), cancellationToken);

    public Task<VersionedCustomer> UpdateAsync(
        Customer customer,
        int expectedRevision,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(repository => repository.UpdateAsync(customer, expectedRevision, cancellationToken), cancellationToken);

    private async Task<T> ExecuteAsync<T>(
        Func<CustomerRepository, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
        await using MhcDbContext context = ScopedPersistence.CreateContext(connectionString);
        return await operation(new CustomerRepository(context));
    }
}

internal sealed class ScopedCatalogItemRepository(
    string connectionString,
    IApplicationWorkGate workGate) : ICatalogItemRepository
{
    public Task<VersionedCatalogItem> AddAsync(CatalogItem item, CancellationToken cancellationToken = default) =>
        ExecuteAsync(repository => repository.AddAsync(item, cancellationToken), cancellationToken);

    public Task<VersionedCatalogItem?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        ExecuteAsync(repository => repository.GetAsync(id, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<VersionedCatalogItem>> SearchAsync(
        string? searchText,
        bool includeArchived,
        int limit,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(repository => repository.SearchAsync(searchText, includeArchived, limit, cancellationToken), cancellationToken);

    public Task<VersionedCatalogItem> UpdateAsync(
        CatalogItem item,
        int expectedRevision,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(repository => repository.UpdateAsync(item, expectedRevision, cancellationToken), cancellationToken);

    private async Task<T> ExecuteAsync<T>(
        Func<CatalogItemRepository, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
        await using MhcDbContext context = ScopedPersistence.CreateContext(connectionString);
        return await operation(new CatalogItemRepository(context));
    }
}

internal sealed class ScopedInvoiceRepository(
    string connectionString,
    IApplicationWorkGate workGate) : IInvoiceRepository
{
    public Task<InvoiceSummary?> GetSummaryAsync(Guid id, CancellationToken cancellationToken = default) =>
        ExecuteAsync(repository => repository.GetSummaryAsync(id, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<InvoiceSummary>> SearchAsync(
        string? searchText,
        DateOnly? fromBusinessDate,
        DateOnly? toBusinessDate,
        int limit,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            repository => repository.SearchAsync(searchText, fromBusinessDate, toBusinessDate, limit, cancellationToken),
            cancellationToken);

    public Task<InvoiceSnapshot?> GetSnapshotAsync(Guid id, CancellationToken cancellationToken = default) =>
        ExecuteAsync(repository => repository.GetSnapshotAsync(id, cancellationToken), cancellationToken);

    public Task<InvoiceDocument?> GetDocumentAsync(Guid id, CancellationToken cancellationToken = default) =>
        ExecuteAsync(repository => repository.GetDocumentAsync(id, cancellationToken), cancellationToken);

    public Task<InvoiceVoidInfo> VoidAsync(
        Guid id,
        string reason,
        string operatorName,
        DateTimeOffset voidedAtUtc,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            repository => repository.VoidAsync(id, reason, operatorName, voidedAtUtc, cancellationToken),
            cancellationToken);

    private async Task<T> ExecuteAsync<T>(
        Func<InvoiceRepository, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
        await using MhcDbContext context = ScopedPersistence.CreateContext(connectionString);
        return await operation(new InvoiceRepository(context));
    }
}

internal sealed class ScopedInvoiceEditorCompanyProfile(
    string connectionString,
    IApplicationWorkGate workGate) : IInvoiceEditorCompanyProfile
{
    public async Task<InvoiceEditorCompanyProfile> GetAsync(CancellationToken cancellationToken = default)
    {
        await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
        await using MhcDbContext context = ScopedPersistence.CreateContext(connectionString);
        VersionedCompanyProfile? profile = await new CompanyProfileRepository(context)
            .GetAsync(cancellationToken);
        return new InvoiceEditorCompanyProfile(profile is not null);
    }
}

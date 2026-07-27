using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Customers;
using MHC.Invoicing.Domain.Search;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Repositories;

public sealed class CustomerRepository(MhcDbContext context) : ICustomerRepository
{
    public async Task<VersionedCustomer> AddAsync(
        Customer customer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);
        CustomerEntity entity = ToEntity(customer, revision: 0);
        context.Customers.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        context.Entry(entity).State = EntityState.Detached;
        return new VersionedCustomer(customer, entity.Revision);
    }

    public async Task<VersionedCustomer?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        CustomerEntity? entity = await context.Customers
            .AsNoTracking()
            .SingleOrDefaultAsync(customer => customer.Id == id, cancellationToken);
        return entity is null ? null : ToVersionedCustomer(entity);
    }

    public async Task<IReadOnlyList<VersionedCustomer>> SearchAsync(
        string? searchText,
        bool includeArchived,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);

        string rawSearch = searchText?.Trim() ?? string.Empty;
        string normalizedSearch = ArabicSearchNormalizer.Normalize(rawSearch);
        IQueryable<CustomerEntity> query = context.Customers.AsNoTracking();
        if (!includeArchived)
        {
            query = query.Where(customer => !customer.IsArchived);
        }

        if (normalizedSearch.Length > 0)
        {
            query = query.Where(customer =>
                customer.SearchNameArabic.Contains(normalizedSearch) ||
                customer.SearchNameEnglish.Contains(normalizedSearch) ||
                (customer.VatNumber != null && customer.VatNumber.Contains(rawSearch)) ||
                (customer.CommercialRegistration != null && customer.CommercialRegistration.Contains(rawSearch)) ||
                (customer.Phone != null && customer.Phone.Contains(rawSearch)));
        }

        List<CustomerEntity> entities = await query
            .OrderBy(customer =>
                normalizedSearch.Length == 0 ||
                customer.SearchNameArabic.StartsWith(normalizedSearch) ||
                customer.SearchNameEnglish.StartsWith(normalizedSearch) ||
                customer.VatNumber == rawSearch ||
                customer.CommercialRegistration == rawSearch
                    ? 0
                    : 1)
            .ThenBy(customer => customer.NameArabic)
            .ThenBy(customer => customer.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return entities.ConvertAll(ToVersionedCustomer).AsReadOnly();
    }

    public async Task<VersionedCustomer> UpdateAsync(
        Customer customer,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);

        CustomerEntity entity = ToEntity(customer, checked(expectedRevision + 1));
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
                $"Customer {customer.Id} was modified or deleted by another operation.",
                exception);
        }
        finally
        {
            context.Entry(entity).State = EntityState.Detached;
        }

        return new VersionedCustomer(customer, entity.Revision);
    }

    private static CustomerEntity ToEntity(Customer customer, int revision) => new()
    {
        Id = customer.Id,
        NameArabic = customer.NameArabic,
        NameEnglish = customer.NameEnglish,
        SearchNameArabic = customer.SearchNameArabic,
        SearchNameEnglish = customer.SearchNameEnglish,
        VatNumber = customer.VatNumber,
        CommercialRegistration = customer.CommercialRegistration,
        Address = customer.Address,
        Phone = customer.Phone,
        Email = customer.Email,
        IsArchived = customer.IsArchived,
        Revision = revision,
        CreatedAtUtcMs = customer.CreatedAtUtc.ToUnixTimeMilliseconds(),
        UpdatedAtUtcMs = customer.UpdatedAtUtc.ToUnixTimeMilliseconds(),
    };

    private static VersionedCustomer ToVersionedCustomer(CustomerEntity entity) => new(
        Customer.Rehydrate(
            entity.Id,
            entity.NameArabic,
            entity.NameEnglish,
            entity.VatNumber,
            entity.CommercialRegistration,
            entity.Address,
            entity.Phone,
            entity.Email,
            DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUtcMs),
            DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUtcMs),
            entity.IsArchived),
        entity.Revision);
}

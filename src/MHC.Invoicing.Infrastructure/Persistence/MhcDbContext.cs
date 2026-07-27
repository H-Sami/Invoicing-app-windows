using System.Text;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Persistence;

public sealed class MhcDbContext(DbContextOptions<MhcDbContext> options) : DbContext(options)
{
    public DbSet<CompanyProfileEntity> CompanyProfiles => Set<CompanyProfileEntity>();

    public DbSet<CustomerEntity> Customers => Set<CustomerEntity>();

    public DbSet<CatalogItemEntity> CatalogItems => Set<CatalogItemEntity>();

    public DbSet<InvoiceDraftEntity> InvoiceDrafts => Set<InvoiceDraftEntity>();

    public DbSet<InvoiceDraftLineEntity> InvoiceDraftLines => Set<InvoiceDraftLineEntity>();

    public DbSet<InvoiceSequenceEntity> InvoiceSequences => Set<InvoiceSequenceEntity>();

    public DbSet<InvoiceEntity> Invoices => Set<InvoiceEntity>();

    public DbSet<InvoiceLineEntity> InvoiceLines => Set<InvoiceLineEntity>();

    public DbSet<InvoiceDocumentEntity> InvoiceDocuments => Set<InvoiceDocumentEntity>();

    public DbSet<InvoiceVoidEntity> InvoiceVoids => Set<InvoiceVoidEntity>();

    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();

    public DbSet<AppSettingEntity> AppSettings => Set<AppSettingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MhcDbContext).Assembly);

        foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableEntityType entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (Microsoft.EntityFrameworkCore.Metadata.IMutableProperty property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        StringBuilder result = new(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }
}

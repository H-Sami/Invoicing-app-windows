using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MHC.Invoicing.Infrastructure.Persistence;

internal sealed class MhcDbContextFactory : IDesignTimeDbContextFactory<MhcDbContext>
{
    public MhcDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite("Data Source=mhc-invoices-design.db")
            .Options;
        return new MhcDbContext(options);
    }
}

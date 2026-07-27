using MHC.Invoicing.App.Documents;
using MHC.Invoicing.Application.Issuance;
using MHC.Invoicing.Infrastructure.Backup;
using MHC.Invoicing.Infrastructure.Documents;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Storage;
using MHC.Invoicing.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.App;

internal sealed class AppComposition
{
    private AppComposition(
        AppDataPaths paths,
        string connectionString,
        InvoiceIssuanceService issuance)
    {
        Paths = paths;
        ConnectionString = connectionString;
        Issuance = issuance;
    }

    internal AppDataPaths Paths { get; }

    internal string ConnectionString { get; }

    internal InvoiceIssuanceService Issuance { get; }

    internal static async Task<AppComposition> CreateAsync(
        WebView2InvoiceDocumentService documentService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentService);
        AppDataPaths paths = AppDataPaths.CreateDefault();
        paths.EnsureDirectoriesExist();
        await BackupService.RecoverInterruptedRestoreAsync(
            paths.DatabasePath,
            paths.InvoicesDirectory,
            cancellationToken).ConfigureAwait(true);
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            ForeignKeys = true,
            DefaultTimeout = 5,
            Pooling = true,
        }.ToString();
        DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using (MhcDbContext context = new(options))
        {
            await new DatabaseInitializer(context).InitializeAsync(cancellationToken).ConfigureAwait(true);
        }

        InvoiceIssuanceService issuance = new(
            connectionString,
            new SaudiClock(),
            new DocumentSerialGenerator(),
            new InvoiceHtmlRenderer(),
            documentService,
            new ZatcaQrGenerator());
        return new AppComposition(paths, connectionString, issuance);
    }
}

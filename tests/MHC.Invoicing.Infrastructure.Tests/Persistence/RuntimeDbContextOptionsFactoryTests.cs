using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Tests.Persistence;

public sealed class RuntimeDbContextOptionsFactoryTests
{
    [Fact]
    public async Task Create_ConfiguresEveryConnectionAndUsesTheApplicationDatabasePath()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string localAppData = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}");
        AppDataPaths paths = AppDataPaths.Create(localAppData);
        try
        {
            DbContextOptions<MhcDbContext> options = RuntimeDbContextOptionsFactory.Create(paths);
            await using (MhcDbContext initializingContext = new(options))
            {
                await new DatabaseInitializer(initializingContext).InitializeAsync(cancellationToken);
            }

            await using MhcDbContext context = new(options);
            await context.Database.OpenConnectionAsync(cancellationToken);
            Assert.Equal(paths.DatabasePath, context.Database.GetDbConnection().DataSource);
            Assert.Equal(1L, await ExecuteScalarAsync(context, "PRAGMA foreign_keys;", cancellationToken));
            Assert.Equal(2L, await ExecuteScalarAsync(context, "PRAGMA synchronous;", cancellationToken));
            Assert.Equal(5_000L, await ExecuteScalarAsync(context, "PRAGMA busy_timeout;", cancellationToken));
            Assert.Equal("wal", await ExecuteTextScalarAsync(context, "PRAGMA journal_mode;", cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(localAppData))
            {
                Directory.Delete(localAppData, recursive: true);
            }
        }
    }

    private static async Task<long> ExecuteScalarAsync(
        DbContext context,
        string sql,
        CancellationToken cancellationToken)
    {
        await using System.Data.Common.DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ExecuteTextScalarAsync(
        DbContext context,
        string sql,
        CancellationToken cancellationToken)
    {
        await using System.Data.Common.DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}

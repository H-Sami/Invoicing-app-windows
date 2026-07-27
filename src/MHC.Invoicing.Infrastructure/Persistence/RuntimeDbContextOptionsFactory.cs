using System.Data.Common;
using MHC.Invoicing.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MHC.Invoicing.Infrastructure.Persistence;

public static class RuntimeDbContextOptionsFactory
{
    private static readonly SqlitePragmaConnectionInterceptor ConnectionInterceptor = new();

    public static DbContextOptions<MhcDbContext> Create(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureDirectoriesExist();

        SqliteConnectionStringBuilder connectionString = new()
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 5,
        };

        return new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite(connectionString.ConnectionString)
            .AddInterceptors(ConnectionInterceptor)
            .Options;
    }
}

internal sealed class SqlitePragmaConnectionInterceptor : DbConnectionInterceptor
{
    private const string ConnectionPragmas =
        "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = ConnectionPragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = ConnectionPragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

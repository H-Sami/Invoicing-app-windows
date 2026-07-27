using MHC.Invoicing.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Tests.Persistence;

public sealed class InvoiceNumberAllocatorTests
{
    [Fact]
    public void Allocator_HasNoStandaloneCommittingApi()
    {
        Assert.True(typeof(InvoiceNumberAllocator).IsAbstract && typeof(InvoiceNumberAllocator).IsSealed);
        Assert.DoesNotContain(typeof(InvoiceNumberAllocator).GetMethods(), method => method.Name == "AllocateAsync");
    }

    [Fact]
    public async Task AllocateWithinTransactionAsync_RollbackDoesNotConsumeNumber()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using TestDatabase database = await TestDatabase.CreateAsync(cancellationToken);
        await using (SqliteConnection connection = new(database.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            Assert.Equal(100, (await InvoiceNumberAllocator.AllocateWithinTransactionAsync(
                2026, connection, transaction, cancellationToken)).Sequence);
            await transaction.RollbackAsync(cancellationToken);
        }

        await using (SqliteConnection connection = new(database.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            Assert.Equal(100, (await InvoiceNumberAllocator.AllocateWithinTransactionAsync(
                2026, connection, transaction, cancellationToken)).Sequence);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _path;
        private TestDatabase(string path)
        {
            _path = path;
            ConnectionString = $"Data Source={path};Default Timeout=10;Foreign Keys=True";
        }
        public string ConnectionString { get; }
        public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
        {
            TestDatabase database = new(Path.Combine(Path.GetTempPath(), $"allocator-{Guid.NewGuid():N}.db"));
            DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
                .UseSqlite(database.ConnectionString).Options;
            await using MhcDbContext context = new(options);
            await context.Database.MigrateAsync(cancellationToken);
            return database;
        }
        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
            }
            return ValueTask.CompletedTask;
        }
    }
}

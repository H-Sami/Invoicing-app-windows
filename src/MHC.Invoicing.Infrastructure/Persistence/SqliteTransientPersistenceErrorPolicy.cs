using MHC.Invoicing.Application.Drafts;
using Microsoft.Data.Sqlite;

namespace MHC.Invoicing.Infrastructure.Persistence;

public sealed class SqliteTransientPersistenceErrorPolicy : ITransientPersistenceErrorPolicy
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    public bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is TimeoutException or IOException ||
            exception is SqliteException { SqliteErrorCode: SqliteBusy or SqliteLocked };
    }
}

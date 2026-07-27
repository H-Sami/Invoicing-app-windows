using MHC.Invoicing.Domain.ValueObjects;
using Microsoft.Data.Sqlite;

namespace MHC.Invoicing.Infrastructure.Persistence;

public static class InvoiceNumberAllocator
{
    internal static async Task<InvoiceNumber> PeekAsync(
        int issuanceYear,
        SqliteConnection connection,
        SqliteTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _ = new InvoiceNumber(issuanceYear, 100);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE((SELECT next_value FROM invoice_sequences WHERE issuance_year = $year), 100);";
        command.Parameters.AddWithValue("$year", issuanceYear);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return new InvoiceNumber(issuanceYear, checked(Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture)));
    }

    public static async Task<InvoiceNumber> AllocateWithinTransactionAsync(
        int issuanceYear,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        _ = new InvoiceNumber(issuanceYear, 100);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException("The transaction must belong to the supplied connection.", nameof(transaction));
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO invoice_sequences (issuance_year, next_value)
            VALUES ($year, 101)
            ON CONFLICT (issuance_year)
            DO UPDATE SET next_value = invoice_sequences.next_value + 1
            RETURNING next_value - 1;
            """;
        command.Parameters.AddWithValue("$year", issuanceYear);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return new InvoiceNumber(
            issuanceYear,
            checked(Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture)));
    }
}

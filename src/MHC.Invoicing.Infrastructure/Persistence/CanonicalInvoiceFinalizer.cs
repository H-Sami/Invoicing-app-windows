using System.Security.Cryptography;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Persistence;

internal static class CanonicalInvoiceFinalizer
{
    internal static async Task FinalizeAsync(
        MhcDbContext context,
        Guid invoiceId,
        long finalizedAtUtcMs,
        byte[] trustedPdfHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trustedPdfHash);

        InvoiceDocumentEntity document = await context.InvoiceDocuments
            .AsNoTracking()
            .SingleAsync(item => item.InvoiceId == invoiceId, cancellationToken);
        byte[] actualHash = SHA256.HashData(document.PdfBytes);
        if (trustedPdfHash.Length != actualHash.Length ||
            document.Sha256.Length != actualHash.Length ||
            document.ByteLength != document.PdfBytes.LongLength ||
            !CryptographicOperations.FixedTimeEquals(trustedPdfHash, actualHash) ||
            !CryptographicOperations.FixedTimeEquals(document.Sha256, actualHash))
        {
            throw new InvalidDataException(
                $"Canonical PDF for invoice {invoiceId} failed finalization integrity validation.");
        }

        int inserted = await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({invoiceId}, {finalizedAtUtcMs});",
            cancellationToken);
        if (inserted != 1)
        {
            throw new InvalidOperationException($"Invoice {invoiceId} could not be finalized.");
        }
    }
}

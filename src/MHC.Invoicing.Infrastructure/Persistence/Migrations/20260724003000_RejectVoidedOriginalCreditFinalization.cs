using System.Globalization;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MHC.Invoicing.Infrastructure.Persistence.Migrations;

[DbContext(typeof(MhcDbContext))]
[Migration("20260724003000_RejectVoidedOriginalCreditFinalization")]
public sealed class RejectVoidedOriginalCreditFinalization : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_invoice_finalizations_validate;");
        migrationBuilder.Sql(InvoiceFinalizationTriggerSql.Create(rejectVoidedOriginal: true));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_invoice_finalizations_validate;");
        migrationBuilder.Sql(InvoiceFinalizationTriggerSql.Create(rejectVoidedOriginal: false));
    }

    internal static class InvoiceFinalizationTriggerSql
    {
        private const string Template =
            """
            CREATE TRIGGER trg_invoice_finalizations_validate
            BEFORE INSERT ON invoice_finalizations
            BEGIN
                SELECT CASE WHEN NOT EXISTS (
                    SELECT 1
                    FROM invoices AS i
                    WHERE i.id = NEW.invoice_id
                      AND i.public_number = 'MHC-' || i.issuance_year || '-' || i.sequence
                      AND (SELECT COUNT(*) FROM invoice_lines AS l WHERE l.invoice_id = i.id) > 0
                      AND (SELECT COUNT(*) FROM invoice_documents AS d WHERE d.invoice_id = i.id) = 1
                      AND i.subtotal_halalah = COALESCE((SELECT SUM(l.net_halalah) FROM invoice_lines AS l WHERE l.invoice_id = i.id), 0)
                      AND i.vat_halalah = COALESCE((SELECT SUM(l.vat_halalah) FROM invoice_lines AS l WHERE l.invoice_id = i.id), 0)
                      AND i.grand_total_halalah = COALESCE((SELECT SUM(l.gross_halalah) FROM invoice_lines AS l WHERE l.invoice_id = i.id), 0)
                      AND i.grand_total_halalah = i.subtotal_halalah + i.vat_halalah
                      AND (
                          (i.document_type = 1
                           AND i.original_invoice_id IS NULL
                           AND NOT EXISTS (
                               SELECT 1
                               FROM invoice_lines AS tax_line
                               WHERE tax_line.invoice_id = i.id
                                 AND tax_line.original_invoice_line_id IS NOT NULL))
                          OR EXISTS (
                              SELECT 1
                              FROM invoices AS original_invoice
                              JOIN invoice_finalizations AS original_finalization
                                ON original_finalization.invoice_id = original_invoice.id
                              WHERE i.document_type = 2
                                AND original_invoice.id = i.original_invoice_id
                                AND original_invoice.document_type = 1
                                {0}
                                AND NOT EXISTS (
                                    SELECT 1
                                    FROM invoice_lines AS credit_line
                                    WHERE credit_line.invoice_id = i.id
                                      AND NOT EXISTS (
                                          SELECT 1
                                          FROM invoice_lines AS original_line
                                          WHERE original_line.id = credit_line.original_invoice_line_id
                                            AND original_line.invoice_id = original_invoice.id))
                                AND NOT EXISTS (
                                    SELECT 1
                                    FROM invoice_lines AS original_line
                                    WHERE original_line.invoice_id = original_invoice.id
                                      AND original_line.quantity_milliunits <
                                          COALESCE((
                                              SELECT SUM(finalized_credit_line.quantity_milliunits)
                                              FROM invoice_lines AS finalized_credit_line
                                              JOIN invoices AS finalized_credit
                                                ON finalized_credit.id = finalized_credit_line.invoice_id
                                              JOIN invoice_finalizations AS finalized_credit_marker
                                                ON finalized_credit_marker.invoice_id = finalized_credit.id
                                              WHERE finalized_credit.document_type = 2
                                                AND finalized_credit_line.original_invoice_line_id = original_line.id), 0)
                                          + COALESCE((
                                              SELECT SUM(new_credit_line.quantity_milliunits)
                                              FROM invoice_lines AS new_credit_line
                                              WHERE new_credit_line.invoice_id = i.id
                                                AND new_credit_line.original_invoice_line_id = original_line.id), 0))))
                      AND EXISTS (
                          SELECT 1 FROM invoice_documents AS d
                          WHERE d.invoice_id = i.id
                            AND d.mime_type = 'application/pdf'
                            AND d.byte_length = length(d.pdf_bytes)
                            AND length(d.sha256) = 32)
                ) THEN RAISE(ABORT, 'invoice cannot be finalized until its immutable snapshot is complete and reconciled') END;
            END;
            """;

        internal static string Create(bool rejectVoidedOriginal)
        {
            string clause = rejectVoidedOriginal
                ? """
                  AND NOT EXISTS (
                                      SELECT 1
                                      FROM invoice_voids AS original_void
                                      WHERE original_void.invoice_id = original_invoice.id)
                  """
                : string.Empty;
            return string.Format(CultureInfo.InvariantCulture, Template, clause);
        }
    }
}

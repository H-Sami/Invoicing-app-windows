using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MHC.Invoicing.Infrastructure.Persistence.Migrations;

[DbContext(typeof(MhcDbContext))]
[Migration("20260724010000_StrengthenIssuanceLedgerValidation")]
public sealed class StrengthenIssuanceLedgerValidation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_invoice_finalizations_validate;");
        migrationBuilder.Sql(FinalizationTrigger);
        migrationBuilder.Sql(AuditInsertValidationTrigger);
        migrationBuilder.Sql(VoidValidationTrigger);
        migrationBuilder.Sql(VoidAuditCreationTrigger);
        migrationBuilder.Sql(
            """
            CREATE TEMP TABLE mhc_schema_v3_guard (ok INTEGER NOT NULL CHECK (ok = 1));
            INSERT INTO mhc_schema_v3_guard(ok)
            SELECT CASE WHEN
                (SELECT COUNT(*) FROM invoices) = (SELECT COUNT(*) FROM invoice_finalizations)
            THEN 1 ELSE 0 END;
            INSERT OR IGNORE INTO invoice_finalizations (invoice_id, finalized_at_utc_ms)
            SELECT invoice_id, finalized_at_utc_ms FROM invoice_finalizations;
            INSERT INTO audit_events
                (id, invoice_id, event_type, occurred_at_utc_ms, operator_name, details_json)
            SELECT lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' ||
                   lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' ||
                   lower(hex(randomblob(6))),
                   v.invoice_id, 3, v.voided_at_utc_ms, v.operator_name,
                   json_object('reason', v.reason)
            FROM invoice_voids AS v
            WHERE NOT EXISTS (
                SELECT 1 FROM audit_events AS ae
                WHERE ae.invoice_id = v.invoice_id AND ae.event_type = 3);
            INSERT INTO mhc_schema_v3_guard(ok)
            SELECT CASE WHEN NOT EXISTS (
                SELECT 1
                FROM invoice_voids AS v
                WHERE (SELECT COUNT(*) FROM audit_events AS ae
                       WHERE ae.invoice_id = v.invoice_id AND ae.event_type = 3) <> 1
                   OR NOT EXISTS (
                       SELECT 1 FROM audit_events AS ae
                       WHERE ae.invoice_id = v.invoice_id
                         AND ae.event_type = 3
                         AND ae.occurred_at_utc_ms = v.voided_at_utc_ms
                         AND ae.operator_name = v.operator_name
                         AND json_valid(ae.details_json)
                         AND json_extract(ae.details_json, '$.reason') = v.reason)
            ) AND NOT EXISTS (
                SELECT 1
                FROM audit_events AS ae
                WHERE ae.event_type = 3
                  AND NOT EXISTS (
                      SELECT 1 FROM invoice_voids AS v
                      WHERE v.invoice_id = ae.invoice_id
                        AND v.voided_at_utc_ms = ae.occurred_at_utc_ms
                        AND v.operator_name = ae.operator_name
                        AND json_valid(ae.details_json)
                        AND json_extract(ae.details_json, '$.reason') = v.reason)
            ) THEN 1 ELSE 0 END;
            DROP TABLE mhc_schema_v3_guard;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_invoice_voids_create_audit;");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_invoice_voids_validate;");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_audit_events_validate_insert;");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_invoice_finalizations_validate;");
        migrationBuilder.Sql(
            RejectVoidedOriginalCreditFinalization.InvoiceFinalizationTriggerSql.Create(
                rejectVoidedOriginal: true));
    }

    private const string FinalizationTrigger =
        """
        CREATE TRIGGER trg_invoice_finalizations_validate
        BEFORE INSERT ON invoice_finalizations
        BEGIN
            SELECT CASE WHEN NOT EXISTS (
                SELECT 1
                FROM invoices AS i
                WHERE i.id = NEW.invoice_id
                  AND typeof(i.issuance_year) = 'integer'
                  AND typeof(i.sequence) = 'integer'
                  AND typeof(i.issued_at_utc_ms) = 'integer'
                  AND typeof(i.subtotal_halalah) = 'integer'
                  AND typeof(i.vat_halalah) = 'integer'
                  AND typeof(i.grand_total_halalah) = 'integer'
                  AND i.public_number = 'MHC-' || CAST(i.issuance_year AS TEXT) || '-' || CAST(i.sequence AS TEXT)
                  AND i.issuance_year = CAST(strftime('%Y', i.issued_at_utc_ms / 1000, 'unixepoch', '+3 hours') AS INTEGER)
                  AND i.issued_saudi_offset_minutes = 180
                  AND i.issued_at_saudi_local =
                      strftime('%Y-%m-%dT%H:%M:%S', i.issued_at_utc_ms / 1000, 'unixepoch', '+3 hours')
                      || printf('.%03d+03:00', i.issued_at_utc_ms % 1000)
                  AND typeof(NEW.finalized_at_utc_ms) = 'integer'
                  AND NEW.finalized_at_utc_ms = i.issued_at_utc_ms
                  AND (SELECT COUNT(*) FROM invoice_lines AS l WHERE l.invoice_id = i.id) > 0
                  AND NOT EXISTS (
                      SELECT 1
                      FROM invoice_lines AS l
                      WHERE l.invoice_id = i.id
                        AND (
                            typeof(l.quantity_milliunits) <> 'integer'
                            OR typeof(l.unit_price_halalah) <> 'integer'
                            OR typeof(l.net_halalah) <> 'integer'
                            OR typeof(l.vat_halalah) <> 'integer'
                            OR typeof(l.gross_halalah) <> 'integer'
                            OR l.quantity_milliunits NOT BETWEEN 1 AND 1000000000
                            OR l.unit_price_halalah < 0
                            OR l.net_halalah < 0
                            OR l.vat_halalah < 0
                            OR NOT (
                                l.net_halalah >= (l.quantity_milliunits * (l.unit_price_halalah % 1000) + 500) / 1000
                                AND (l.net_halalah - (l.quantity_milliunits * (l.unit_price_halalah % 1000) + 500) / 1000)
                                    / l.quantity_milliunits = l.unit_price_halalah / 1000
                                AND (l.net_halalah - (l.quantity_milliunits * (l.unit_price_halalah % 1000) + 500) / 1000)
                                    % l.quantity_milliunits = 0
                            )
                            OR (l.vat_category = 1 AND l.vat_halalah <>
                                (l.net_halalah / 100) * 15 + (((l.net_halalah % 100) * 15 + 50) / 100))
                            OR (l.vat_category IN (2, 3) AND l.vat_halalah <> 0)
                            OR l.gross_halalah <> l.net_halalah + l.vat_halalah
                            OR l.vat_category NOT IN (1, 2, 3)
                            OR (l.vat_category = 1 AND
                                (l.tax_exemption_reason_code IS NOT NULL OR l.tax_exemption_reason IS NOT NULL))
                            OR (l.vat_category IN (2, 3) AND (
                                l.tax_exemption_reason_code IS NULL
                                OR trim(l.tax_exemption_reason_code) = ''
                                OR l.tax_exemption_reason IS NULL
                                OR trim(l.tax_exemption_reason) = ''))
                        )
                  )
                  AND i.subtotal_halalah = (SELECT SUM(l.net_halalah) FROM invoice_lines AS l WHERE l.invoice_id = i.id)
                  AND i.vat_halalah = (SELECT SUM(l.vat_halalah) FROM invoice_lines AS l WHERE l.invoice_id = i.id)
                  AND i.grand_total_halalah = (SELECT SUM(l.gross_halalah) FROM invoice_lines AS l WHERE l.invoice_id = i.id)
                  AND i.grand_total_halalah = i.subtotal_halalah + i.vat_halalah
                  AND (SELECT COUNT(*) FROM invoice_documents AS d WHERE d.invoice_id = i.id) = 1
                  AND EXISTS (
                      SELECT 1
                      FROM invoice_documents AS d
                      WHERE d.invoice_id = i.id
                        AND d.mime_type = 'application/pdf'
                        AND typeof(d.byte_length) = 'integer'
                        AND d.byte_length > 0
                        AND d.byte_length = length(d.pdf_bytes)
                        AND length(d.sha256) = 32
                        AND typeof(d.created_at_utc_ms) = 'integer'
                        AND d.created_at_utc_ms = i.issued_at_utc_ms
                  )
                  AND (SELECT COUNT(*) FROM audit_events AS ae
                       WHERE ae.invoice_id = i.id AND ae.event_type IN (1, 2)) = 1
                  AND EXISTS (
                      SELECT 1
                      FROM audit_events AS ae
                      WHERE ae.invoice_id = i.id
                        AND ae.event_type = CASE i.document_type WHEN 1 THEN 1 WHEN 2 THEN 2 END
                        AND typeof(ae.occurred_at_utc_ms) = 'integer'
                        AND ae.occurred_at_utc_ms = i.issued_at_utc_ms
                        AND ae.operator_name = i.operator_name
                  )
                  AND (
                      (i.document_type = 1
                       AND i.original_invoice_id IS NULL
                       AND NOT EXISTS (
                           SELECT 1 FROM invoice_lines AS tax_line
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
                            AND NOT EXISTS (
                                SELECT 1 FROM invoice_voids AS original_void
                                WHERE original_void.invoice_id = original_invoice.id)
                            AND NOT EXISTS (
                                SELECT 1
                                FROM invoice_lines AS credit_line
                                WHERE credit_line.invoice_id = i.id
                                  AND NOT EXISTS (
                                      SELECT 1
                                      FROM invoice_lines AS original_line
                                      WHERE original_line.id = credit_line.original_invoice_line_id
                                        AND original_line.invoice_id = original_invoice.id
                                        AND original_line.unit_price_halalah = credit_line.unit_price_halalah
                                        AND original_line.vat_category = credit_line.vat_category
                                        AND original_line.tax_exemption_reason_code IS credit_line.tax_exemption_reason_code
                                        AND original_line.tax_exemption_reason IS credit_line.tax_exemption_reason))
                            AND NOT EXISTS (
                                SELECT 1
                                FROM invoice_lines AS original_line
                                WHERE original_line.invoice_id = original_invoice.id
                                  AND original_line.quantity_milliunits <
                                      COALESCE((
                                          SELECT SUM(finalized_credit_line.quantity_milliunits)
                                          FROM invoice_lines AS finalized_credit_line
                                          JOIN invoices AS finalized_credit ON finalized_credit.id = finalized_credit_line.invoice_id
                                          JOIN invoice_finalizations AS finalized_credit_marker
                                            ON finalized_credit_marker.invoice_id = finalized_credit.id
                                          WHERE finalized_credit.document_type = 2
                                            AND finalized_credit_line.original_invoice_line_id = original_line.id), 0)
                                      + COALESCE((
                                          SELECT SUM(new_credit_line.quantity_milliunits)
                                          FROM invoice_lines AS new_credit_line
                                          WHERE new_credit_line.invoice_id = i.id
                                            AND new_credit_line.original_invoice_line_id = original_line.id), 0))
                            AND NOT EXISTS (
                                SELECT 1
                                FROM invoice_lines AS original_line
                                WHERE original_line.invoice_id = original_invoice.id
                                  AND (
                                      original_line.net_halalah <
                                        COALESCE((SELECT SUM(credited_line.net_halalah)
                                                  FROM invoice_lines AS credited_line
                                                  JOIN invoices AS credited_invoice ON credited_invoice.id = credited_line.invoice_id
                                                  JOIN invoice_finalizations AS credited_marker ON credited_marker.invoice_id = credited_invoice.id
                                                  WHERE credited_invoice.document_type = 2
                                                    AND credited_line.original_invoice_line_id = original_line.id), 0)
                                        + COALESCE((SELECT SUM(new_line.net_halalah) FROM invoice_lines AS new_line
                                                    WHERE new_line.invoice_id = i.id
                                                      AND new_line.original_invoice_line_id = original_line.id), 0)
                                      OR original_line.vat_halalah <
                                        COALESCE((SELECT SUM(credited_line.vat_halalah)
                                                  FROM invoice_lines AS credited_line
                                                  JOIN invoices AS credited_invoice ON credited_invoice.id = credited_line.invoice_id
                                                  JOIN invoice_finalizations AS credited_marker ON credited_marker.invoice_id = credited_invoice.id
                                                  WHERE credited_invoice.document_type = 2
                                                    AND credited_line.original_invoice_line_id = original_line.id), 0)
                                        + COALESCE((SELECT SUM(new_line.vat_halalah) FROM invoice_lines AS new_line
                                                    WHERE new_line.invoice_id = i.id
                                                      AND new_line.original_invoice_line_id = original_line.id), 0)
                                      OR original_line.gross_halalah <
                                        COALESCE((SELECT SUM(credited_line.gross_halalah)
                                                  FROM invoice_lines AS credited_line
                                                  JOIN invoices AS credited_invoice ON credited_invoice.id = credited_line.invoice_id
                                                  JOIN invoice_finalizations AS credited_marker ON credited_marker.invoice_id = credited_invoice.id
                                                  WHERE credited_invoice.document_type = 2
                                                    AND credited_line.original_invoice_line_id = original_line.id), 0)
                                        + COALESCE((SELECT SUM(new_line.gross_halalah) FROM invoice_lines AS new_line
                                                    WHERE new_line.invoice_id = i.id
                                                      AND new_line.original_invoice_line_id = original_line.id), 0)))
                      )
                  )
            ) THEN RAISE(ABORT, 'invoice cannot be finalized until its immutable snapshot is complete and reconciled') END;
        END;
        """;

    private const string AuditInsertValidationTrigger =
        """
        CREATE TRIGGER trg_audit_events_validate_insert
        BEFORE INSERT ON audit_events
        BEGIN
            SELECT CASE WHEN NEW.invoice_id IS NOT NULL
                              AND NEW.event_type IN (1, 2)
                              AND (EXISTS (SELECT 1 FROM invoice_finalizations AS f WHERE f.invoice_id = NEW.invoice_id)
                                   OR EXISTS (SELECT 1 FROM audit_events AS ae
                                              WHERE ae.invoice_id = NEW.invoice_id AND ae.event_type IN (1, 2)))
                THEN RAISE(ABORT, 'issuance audit evidence must be unique and precede finalization') END;

            SELECT CASE WHEN NEW.event_type = 3 AND NOT (
                NEW.invoice_id IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM audit_events AS ae
                                WHERE ae.invoice_id = NEW.invoice_id AND ae.event_type = 3)
                AND typeof(NEW.occurred_at_utc_ms) = 'integer'
                AND trim(NEW.operator_name) <> ''
                AND json_valid(NEW.details_json)
                AND json_type(NEW.details_json, '$') = 'object'
                AND EXISTS (
                    SELECT 1 FROM invoice_voids AS v
                    WHERE v.invoice_id = NEW.invoice_id
                      AND v.voided_at_utc_ms = NEW.occurred_at_utc_ms
                      AND v.operator_name = NEW.operator_name
                      AND json_extract(NEW.details_json, '$.reason') = v.reason)
            ) THEN RAISE(ABORT, 'void audit evidence must uniquely match an existing void record') END;
        END;
        """;

    private const string VoidValidationTrigger =
        """
        CREATE TRIGGER trg_invoice_voids_validate
        BEFORE INSERT ON invoice_voids
        BEGIN
            SELECT CASE WHEN NOT EXISTS (
                SELECT 1
                FROM invoices AS target
                JOIN invoice_finalizations AS target_finalization ON target_finalization.invoice_id = target.id
                WHERE target.id = NEW.invoice_id
            ) THEN RAISE(ABORT, 'only finalized invoices can be voided') END;

            SELECT CASE WHEN typeof(NEW.voided_at_utc_ms) <> 'integer'
                              OR trim(NEW.reason) = ''
                              OR trim(NEW.operator_name) = ''
                THEN RAISE(ABORT, 'void evidence fields are invalid') END;

            SELECT CASE WHEN (SELECT COUNT(*) FROM audit_events AS ae
                              WHERE ae.invoice_id = NEW.invoice_id AND ae.event_type = 3) > 1
                              OR ((SELECT COUNT(*) FROM audit_events AS ae
                                   WHERE ae.invoice_id = NEW.invoice_id AND ae.event_type = 3) = 1
                                  AND NOT EXISTS (
                                      SELECT 1 FROM audit_events AS ae
                                      WHERE ae.invoice_id = NEW.invoice_id
                                        AND ae.event_type = 3
                                        AND ae.occurred_at_utc_ms = NEW.voided_at_utc_ms
                                        AND ae.operator_name = NEW.operator_name
                                        AND json_valid(ae.details_json)
                                        AND json_type(ae.details_json, '$') = 'object'
                                        AND json_extract(ae.details_json, '$.reason') = NEW.reason))
                THEN RAISE(ABORT, 'existing void audit evidence does not match') END;

            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM invoices AS target
                WHERE target.id = NEW.invoice_id
                  AND target.document_type = 1
                  AND EXISTS (
                      SELECT 1
                      FROM invoices AS credit
                      JOIN invoice_finalizations AS credit_finalization ON credit_finalization.invoice_id = credit.id
                      WHERE credit.document_type = 2
                        AND credit.original_invoice_id = target.id)
            ) THEN RAISE(ABORT, 'a tax invoice with finalized credit notes cannot be voided') END;
        END;
        """;

    private const string VoidAuditCreationTrigger =
        """
        CREATE TRIGGER trg_invoice_voids_create_audit
        AFTER INSERT ON invoice_voids
        WHEN NOT EXISTS (
            SELECT 1 FROM audit_events AS ae
            WHERE ae.invoice_id = NEW.invoice_id AND ae.event_type = 3)
        BEGIN
            INSERT INTO audit_events
                (id, invoice_id, event_type, occurred_at_utc_ms, operator_name, details_json)
            VALUES
                (lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' ||
                 lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' ||
                 lower(hex(randomblob(6))),
                 NEW.invoice_id, 3, NEW.voided_at_utc_ms, NEW.operator_name,
                 json_object('reason', NEW.reason));
        END;
        """;
}

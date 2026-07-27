using System.Security.Cryptography;
using System.Text.Json;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Tests.Persistence;

public sealed class MigrationTests
{
    [Fact]
    public async Task InitialMigration_CreatesSchemaAndImmutabilityTriggers()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);

            string[] tables = await ReadObjectNamesAsync(context, "table", cancellationToken);
            string[] triggers = await ReadObjectNamesAsync(context, "trigger", cancellationToken);

            Assert.Contains("invoices", tables);
            Assert.Contains("invoice_lines", tables);
            Assert.Contains("invoice_documents", tables);
            Assert.Contains("invoice_finalizations", tables);
            Assert.Equal(
                [
                    "trg_audit_events_no_delete",
                    "trg_audit_events_no_update",
                    "trg_audit_events_validate_insert",
                    "trg_invoice_documents_no_delete",
                    "trg_invoice_documents_no_insert_after_finalization",
                    "trg_invoice_documents_no_update",
                    "trg_invoice_finalizations_no_delete",
                    "trg_invoice_finalizations_no_update",
                    "trg_invoice_finalizations_validate",
                    "trg_invoice_lines_no_delete",
                    "trg_invoice_lines_no_insert_after_finalization",
                    "trg_invoice_lines_no_update",
                    "trg_invoice_voids_create_audit",
                    "trg_invoice_voids_no_delete",
                    "trg_invoice_voids_no_update",
                    "trg_invoice_voids_validate",
                    "trg_invoices_no_delete",
                    "trg_invoices_no_update",
                ],
                triggers);

            int violations = await CountForeignKeyViolationsAsync(context, cancellationToken);
            Assert.Equal(0, violations);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task SchemaThreeUpgrade_BackfillsLegacyVoidAndDowngradeRestoresExactSchemaTwoTrigger()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        const string schemaTwo = "20260724003000_RejectVoidedOriginalCreditFinalization";
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(schemaTwo, cancellationToken);
            string canonicalSchemaTwoTrigger = await ReadTriggerSqlAsync(context, cancellationToken);
            InvoiceEntity invoice = CreateIssuedInvoice();
            context.Invoices.Add(invoice);
            context.AuditEvents.Add(new AuditEventEntity
            {
                Id = Guid.CreateVersion7(),
                InvoiceId = invoice.Id,
                EventType = 1,
                OccurredAtUtcMs = invoice.IssuedAtUtcMs,
                OperatorName = invoice.OperatorName,
            });
            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({invoice.Id}, {invoice.IssuedAtUtcMs});",
                cancellationToken);
            string reason = "Customer said \"replace\"\nlegacy correction";
            long voidedAt = invoice.IssuedAtUtcMs + 1;
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_voids (invoice_id, reason, voided_at_utc_ms, operator_name) VALUES ({invoice.Id}, {reason}, {voidedAt}, {invoice.OperatorName});",
                cancellationToken);

            await context.Database.MigrateAsync(cancellationToken);
            context.ChangeTracker.Clear();
            AuditEventEntity evidence = await context.AuditEvents.SingleAsync(
                item => item.InvoiceId == invoice.Id && item.EventType == 3,
                cancellationToken);
            Assert.Equal(voidedAt, evidence.OccurredAtUtcMs);
            Assert.Equal(invoice.OperatorName, evidence.OperatorName);
            using (JsonDocument details = JsonDocument.Parse(evidence.DetailsJson!))
                Assert.Equal(reason, details.RootElement.GetProperty("reason").GetString());

            await context.Database.MigrateAsync(schemaTwo, cancellationToken);
            Assert.Equal(canonicalSchemaTwoTrigger, await ReadTriggerSqlAsync(context, cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task InitialMigration_RejectsIssuedInvoiceUpdatesAndDeletes()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity invoice = CreateIssuedInvoice();
            context.Invoices.Add(invoice);
            context.AuditEvents.Add(new AuditEventEntity
            {
                Id = Guid.CreateVersion7(),
                InvoiceId = invoice.Id,
                EventType = 1,
                OccurredAtUtcMs = invoice.IssuedAtUtcMs,
                OperatorName = invoice.OperatorName,
            });
            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({invoice.Id}, {invoice.IssuedAtUtcMs});",
                cancellationToken);
            string voidReason = "Correction required";
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_voids (invoice_id, reason, voided_at_utc_ms, operator_name) VALUES ({invoice.Id}, {voidReason}, {invoice.IssuedAtUtcMs + 1}, {invoice.OperatorName});",
                cancellationToken);

            string[] forbiddenMutations =
            [
                "UPDATE invoices SET title = 'tampered';",
                "DELETE FROM invoices;",
                "UPDATE invoice_lines SET description = 'tampered';",
                "DELETE FROM invoice_lines;",
                "UPDATE invoice_documents SET mime_type = 'tampered';",
                "DELETE FROM invoice_documents;",
                "UPDATE invoice_voids SET reason = 'tampered';",
                "DELETE FROM invoice_voids;",
                "UPDATE audit_events SET details_json = 'tampered';",
                "DELETE FROM audit_events;",
                "UPDATE invoice_finalizations SET finalized_at_utc_ms = 1;",
                "DELETE FROM invoice_finalizations;",
            ];
            foreach (string sql in forbiddenMutations)
            {
                await Assert.ThrowsAsync<SqliteException>(() =>
                    ExecuteAsync(context, sql, cancellationToken));
            }

            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO invoice_lines
                        (id, invoice_id, position, description, unit, quantity_milliunits,
                         unit_price_halalah, vat_category, net_halalah, vat_halalah, gross_halalah)
                    VALUES
                        ({Guid.CreateVersion7()}, {invoice.Id}, 1, {"late line"}, {"unit"}, 1000,
                         100, {(int)VatCategory.Standard15}, 100, 15, 115);
                    """,
                    cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Finalization_RejectsTaxInvoiceLineLinkedToAnOriginalLine()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity firstTaxInvoice = CreateIssuedInvoice();
            InvoiceEntity malformedTaxInvoice = CreateIssuedInvoice(sequence: 101);
            malformedTaxInvoice.Lines.Single().OriginalInvoiceLineId = firstTaxInvoice.Lines.Single().Id;
            context.Invoices.AddRange(firstTaxInvoice, malformedTaxInvoice);
            await context.SaveChangesAsync(cancellationToken);

            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({malformedTaxInvoice.Id}, {malformedTaxInvoice.IssuedAtUtcMs});",
                    cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Finalization_RejectsCreditLineWithDifferentEconomicTermsThanOriginal()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity original = CreateIssuedInvoice();
            InvoiceEntity credit = CreateCreditNote(original, sequence: 101, quantityMilliunits: 1_000);
            InvoiceLineEntity creditLine = credit.Lines.Single();
            creditLine.UnitPriceHalalah = 200;
            creditLine.NetHalalah = 200;
            creditLine.VatHalalah = 30;
            creditLine.GrossHalalah = 230;
            credit.SubtotalHalalah = 200;
            credit.VatHalalah = 30;
            credit.GrandTotalHalalah = 230;
            context.Invoices.AddRange(original, credit);
            context.AuditEvents.AddRange(IssuanceAudit(original), IssuanceAudit(credit));
            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({original.Id}, {original.IssuedAtUtcMs});",
                cancellationToken);

            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({credit.Id}, {credit.IssuedAtUtcMs});",
                cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task AuditTrigger_RejectsSecondIssuanceAuditAfterFinalization()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity invoice = CreateIssuedInvoice();
            context.Invoices.Add(invoice);
            context.AuditEvents.Add(IssuanceAudit(invoice));
            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({invoice.Id}, {invoice.IssuedAtUtcMs});",
                cancellationToken);

            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO audit_events (id, invoice_id, event_type, occurred_at_utc_ms, operator_name, details_json)
                VALUES ({Guid.CreateVersion7()}, {invoice.Id}, 1, {invoice.IssuedAtUtcMs}, {invoice.OperatorName}, NULL);
                """,
                cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task VoidInsert_AtomicallyCreatesMatchingAuditEvidence()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity invoice = CreateIssuedInvoice();
            context.Invoices.Add(invoice);
            context.AuditEvents.Add(IssuanceAudit(invoice));
            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({invoice.Id}, {invoice.IssuedAtUtcMs});",
                cancellationToken);
            string reason = "Correction required";
            long voidedAt = invoice.IssuedAtUtcMs + 1;

            int inserted = await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_voids (invoice_id, reason, voided_at_utc_ms, operator_name) VALUES ({invoice.Id}, {reason}, {voidedAt}, {invoice.OperatorName});",
                cancellationToken);

            Assert.Equal(1, inserted);
            AuditEventEntity audit = await context.AuditEvents.AsNoTracking().SingleAsync(
                entry => entry.InvoiceId == invoice.Id && entry.EventType == 3,
                cancellationToken);
            Assert.Equal(voidedAt, audit.OccurredAtUtcMs);
            Assert.Equal(invoice.OperatorName, audit.OperatorName);
            Assert.NotNull(audit.DetailsJson);
            Assert.Equal(reason, System.Text.Json.JsonDocument.Parse(audit.DetailsJson)
                .RootElement.GetProperty("reason").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task AuditInsert_RejectsOrphanVoidAudit()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity invoice = CreateIssuedInvoice();
            context.Invoices.Add(invoice);
            context.AuditEvents.Add(IssuanceAudit(invoice));
            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({invoice.Id}, {invoice.IssuedAtUtcMs});",
                cancellationToken);
            string reasonJson = System.Text.Json.JsonSerializer.Serialize(new { reason = "Correction required" });

            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO audit_events (id, invoice_id, event_type, occurred_at_utc_ms, operator_name, details_json) VALUES ({Guid.CreateVersion7()}, {invoice.Id}, {3}, {invoice.IssuedAtUtcMs + 1}, {invoice.OperatorName}, {reasonJson});",
                cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Finalization_RejectsCumulativeCreditedQuantityAboveOriginalLineQuantity()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity original = CreateIssuedInvoice();
            InvoiceEntity firstCredit = CreateCreditNote(original, sequence: 101, quantityMilliunits: 600);
            InvoiceEntity excessiveCredit = CreateCreditNote(original, sequence: 102, quantityMilliunits: 600);
            context.Invoices.AddRange(original, firstCredit, excessiveCredit);
            context.AuditEvents.AddRange(
                IssuanceAudit(original), IssuanceAudit(firstCredit), IssuanceAudit(excessiveCredit));
            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({original.Id}, {original.IssuedAtUtcMs});",
                cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({firstCredit.Id}, {firstCredit.IssuedAtUtcMs});",
                cancellationToken);

            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({excessiveCredit.Id}, {excessiveCredit.IssuedAtUtcMs});",
                    cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Finalization_RejectsCreditLineFromDifferentInvoiceThanDeclaredOriginal()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity declaredOriginal = CreateIssuedInvoice();
            InvoiceEntity differentInvoice = CreateIssuedInvoice(sequence: 101);
            InvoiceEntity creditNote = CreateCreditNote(declaredOriginal, sequence: 102, quantityMilliunits: 1_000);
            creditNote.Lines.Single().OriginalInvoiceLineId = differentInvoice.Lines.Single().Id;
            context.Invoices.AddRange(declaredOriginal, differentInvoice, creditNote);
            context.AuditEvents.AddRange(
                IssuanceAudit(declaredOriginal), IssuanceAudit(differentInvoice), IssuanceAudit(creditNote));
            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({declaredOriginal.Id}, {declaredOriginal.IssuedAtUtcMs});",
                cancellationToken);

            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({creditNote.Id}, {creditNote.IssuedAtUtcMs});",
                    cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Finalization_RejectsCreditNoteWhoseFinalizedOriginalIsAnotherCreditNote()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity taxInvoice = CreateIssuedInvoice();
            InvoiceEntity firstCredit = CreateCreditNote(taxInvoice, sequence: 101, quantityMilliunits: 500);
            InvoiceEntity nestedCredit = CreateCreditNote(firstCredit, sequence: 102, quantityMilliunits: 500);
            context.Invoices.AddRange(taxInvoice, firstCredit, nestedCredit);
            context.AuditEvents.AddRange(
                IssuanceAudit(taxInvoice), IssuanceAudit(firstCredit), IssuanceAudit(nestedCredit));
            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({taxInvoice.Id}, {taxInvoice.IssuedAtUtcMs});",
                cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({firstCredit.Id}, {firstCredit.IssuedAtUtcMs});",
                cancellationToken);

            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({nestedCredit.Id}, {nestedCredit.IssuedAtUtcMs});",
                    cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Finalization_RejectsCreditNoteWhoseOriginalTaxInvoiceIsNotFinalized()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity original = CreateIssuedInvoice();
            InvoiceEntity creditNote = CreateCreditNote(original, sequence: 101, quantityMilliunits: 1_000);
            context.Invoices.AddRange(original, creditNote);
            await context.SaveChangesAsync(cancellationToken);

            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({creditNote.Id}, {creditNote.IssuedAtUtcMs});",
                    cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Finalization_RejectsCreditNoteWhoseOriginalTaxInvoiceIsVoided()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity original = CreateIssuedInvoice();
            InvoiceEntity creditNote = CreateCreditNote(original, sequence: 101, quantityMilliunits: 1_000);
            context.Invoices.AddRange(original, creditNote);
            context.AuditEvents.AddRange(IssuanceAudit(original), IssuanceAudit(creditNote));
            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({original.Id}, {original.IssuedAtUtcMs});",
                cancellationToken);
            string originalVoidReason = "Correction required";
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_voids (invoice_id, reason, voided_at_utc_ms, operator_name) VALUES ({original.Id}, {originalVoidReason}, {original.IssuedAtUtcMs + 1}, {original.OperatorName});",
                cancellationToken);

            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({creditNote.Id}, {creditNote.IssuedAtUtcMs});",
                    cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Finalization_RejectsIncompleteIssuedRecords()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity invoice = CreateIssuedInvoice();
            invoice.Document = null;
            context.Invoices.Add(invoice);
            await context.SaveChangesAsync(cancellationToken);

            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({invoice.Id}, {invoice.IssuedAtUtcMs});",
                    cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task CanonicalFinalization_RejectsStoredHashThatDoesNotMatchTrustedPdfBytes()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity invoice = CreateIssuedInvoice();
            byte[] canonicalPdf = "%PDF-1.7 canonical"u8.ToArray();
            byte[] trustedHash = SHA256.HashData(canonicalPdf);
            invoice.Document!.PdfBytes = canonicalPdf;
            invoice.Document.ByteLength = canonicalPdf.Length;
            invoice.Document.Sha256 = SHA256.HashData("%PDF-1.7 tampered"u8);
            context.Invoices.Add(invoice);
            await context.SaveChangesAsync(cancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                CanonicalInvoiceFinalizer.FinalizeAsync(
                    context, invoice.Id, invoice.IssuedAtUtcMs, trustedHash, cancellationToken));

            int finalized = await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM invoice_finalizations").SingleAsync(cancellationToken);
            Assert.Equal(0, finalized);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Finalization_RejectsIncorrectRoundedNetEvenWhenHeaderTotalsReconcile()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity invoice = CreateIssuedInvoice();
            InvoiceLineEntity line = invoice.Lines.Single();
            line.QuantityMilliunits = 500;
            line.UnitPriceHalalah = 1;
            line.NetHalalah = 0;
            line.VatHalalah = 0;
            line.GrossHalalah = 0;
            invoice.SubtotalHalalah = 0;
            invoice.VatHalalah = 0;
            invoice.GrandTotalHalalah = 0;
            context.Invoices.Add(invoice);
            context.AuditEvents.Add(IssuanceAudit(invoice));
            await context.SaveChangesAsync(cancellationToken);

            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({invoice.Id}, {invoice.IssuedAtUtcMs});",
                cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Finalization_RejectsMissingIssuanceAudit()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity invoice = CreateIssuedInvoice();
            context.Invoices.Add(invoice);
            await context.SaveChangesAsync(cancellationToken);

            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({invoice.Id}, {invoice.IssuedAtUtcMs});",
                cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task VoidTrigger_RejectsTaxInvoiceWithFinalizedCreditNote()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity original = CreateIssuedInvoice();
            InvoiceEntity credit = CreateCreditNote(original, sequence: 101, quantityMilliunits: 1_000);
            context.Invoices.AddRange(original, credit);
            context.AuditEvents.AddRange(IssuanceAudit(original), IssuanceAudit(credit));
            await context.SaveChangesAsync(cancellationToken);
            foreach (InvoiceEntity invoice in new[] { original, credit })
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({invoice.Id}, {invoice.IssuedAtUtcMs});",
                    cancellationToken);
            }

            string reason = "Correction required";
            string operatorName = "Operator";
            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_voids (invoice_id, reason, voided_at_utc_ms, operator_name) VALUES ({original.Id}, {reason}, {original.IssuedAtUtcMs + 1}, {operatorName});",
                cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Theory]
    [InlineData("vat-rounding")]
    [InlineData("standard-exemption")]
    [InlineData("saudi-time")]
    [InlineData("document-time")]
    [InlineData("audit-operator")]
    [InlineData("finalization-time")]
    public async Task Finalization_RejectsSemanticLedgerMismatch(string mutation)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string databasePath = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.db");
        try
        {
            await using MhcDbContext context = CreateContext(databasePath);
            await context.Database.MigrateAsync(cancellationToken);
            InvoiceEntity invoice = CreateIssuedInvoice();
            InvoiceLineEntity line = invoice.Lines.Single();
            AuditEventEntity audit = IssuanceAudit(invoice);
            long finalizedAt = invoice.IssuedAtUtcMs;
            switch (mutation)
            {
                case "vat-rounding":
                    line.UnitPriceHalalah = 10;
                    line.NetHalalah = 10;
                    line.VatHalalah = 1;
                    line.GrossHalalah = 11;
                    invoice.SubtotalHalalah = 10;
                    invoice.VatHalalah = 1;
                    invoice.GrandTotalHalalah = 11;
                    break;
                case "standard-exemption":
                    line.TaxExemptionReasonCode = "EX";
                    line.TaxExemptionReason = "Not allowed";
                    break;
                case "saudi-time":
                    invoice.IssuedAtSaudiLocal = "2026-03-25T20:40:00.001+03:00";
                    break;
                case "document-time":
                    invoice.Document!.CreatedAtUtcMs++;
                    break;
                case "audit-operator":
                    audit.OperatorName = "Different operator";
                    break;
                case "finalization-time":
                    finalizedAt++;
                    break;
            }
            context.Invoices.Add(invoice);
            context.AuditEvents.Add(audit);
            await context.SaveChangesAsync(cancellationToken);

            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO invoice_finalizations (invoice_id, finalized_at_utc_ms) VALUES ({invoice.Id}, {finalizedAt});",
                cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static MhcDbContext CreateContext(string databasePath)
    {
        DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new MhcDbContext(options);
    }

    private static AuditEventEntity IssuanceAudit(InvoiceEntity invoice) => new()
    {
        Id = Guid.CreateVersion7(),
        InvoiceId = invoice.Id,
        EventType = invoice.DocumentType == InvoiceDocumentType.TaxInvoice ? 1 : 2,
        OccurredAtUtcMs = invoice.IssuedAtUtcMs,
        OperatorName = invoice.OperatorName,
    };

    private static InvoiceEntity CreateCreditNote(
        InvoiceEntity original,
        int sequence,
        long quantityMilliunits)
    {
        InvoiceEntity creditNote = CreateIssuedInvoice();
        creditNote.Id = Guid.CreateVersion7();
        creditNote.Sequence = sequence;
        creditNote.PublicNumber = $"MHC-2026-{sequence}";
        creditNote.DocumentType = InvoiceDocumentType.CreditNote;
        creditNote.OriginalInvoiceId = original.Id;
        InvoiceLineEntity line = creditNote.Lines.Single();
        line.OriginalInvoiceLineId = original.Lines.Single().Id;
        line.QuantityMilliunits = quantityMilliunits;
        line.NetHalalah = (quantityMilliunits * line.UnitPriceHalalah + 500) / 1_000;
        line.VatHalalah = (line.NetHalalah * 15 + 50) / 100;
        line.GrossHalalah = line.NetHalalah + line.VatHalalah;
        creditNote.SubtotalHalalah = line.NetHalalah;
        creditNote.VatHalalah = line.VatHalalah;
        creditNote.GrandTotalHalalah = line.GrossHalalah;
        return creditNote;
    }

    private static InvoiceEntity CreateIssuedInvoice(int sequence = 100, bool isVoided = false)
    {
        InvoiceEntity invoice = new()
        {
            Id = Guid.CreateVersion7(),
            IssuanceYear = 2026,
            Sequence = sequence,
            PublicNumber = $"MHC-2026-{sequence}",
            DocumentType = InvoiceDocumentType.TaxInvoice,
            BusinessDate = "2026-07-23",
            IssuedAtUtcMs = 1_774_460_400_000,
            IssuedAtSaudiLocal = "2026-03-25T20:40:00.000+03:00",
            IssuedSaudiOffsetMinutes = 180,
            SellerNameArabic = "MHC Technology",
            SellerVatNumber = "310123456789003",
            SellerBranch = "Riyadh",
            SellerAddress = "Riyadh",
            OperatorName = "Operator",
            CustomerNameArabic = "Customer",
            CustomerSearchName = "customer",
            PaymentMethod = PaymentMethod.Cash,
            Currency = "SAR",
            SubtotalHalalah = 100,
            VatHalalah = 15,
            GrandTotalHalalah = 115,
        };
        invoice.Lines.Add(new InvoiceLineEntity
        {
            Id = Guid.CreateVersion7(),
            Position = 0,
            Description = "Service",
            Unit = "unit",
            QuantityMilliunits = 1_000,
            UnitPriceHalalah = 100,
            VatCategory = VatCategory.Standard15,
            NetHalalah = 100,
            VatHalalah = 15,
            GrossHalalah = 115,
        });
        invoice.Document = new InvoiceDocumentEntity
        {
            PdfBytes = [0x25],
            Sha256 = new byte[32],
            ByteLength = 1,
            CreatedAtUtcMs = invoice.IssuedAtUtcMs,
        };
        if (isVoided)
        {
            invoice.Void = new InvoiceVoidEntity
            {
                Reason = "Correction required",
                VoidedAtUtcMs = invoice.IssuedAtUtcMs + 1,
                OperatorName = "Operator",
            };
        }
        return invoice;
    }

    private static async Task<string[]> ReadObjectNamesAsync(
        DbContext context,
        string objectType,
        CancellationToken cancellationToken)
    {
        await using System.Data.Common.DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        System.Data.Common.DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "$type";
        parameter.Value = objectType;
        command.Parameters.Add(parameter);

        await context.Database.OpenConnectionAsync(cancellationToken);
        List<string> names = [];
        await using System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }

    private static async Task<string> ReadTriggerSqlAsync(
        DbContext context,
        CancellationToken cancellationToken)
    {
        await using System.Data.Common.DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type = 'trigger' AND name = 'trg_invoice_finalizations_validate';";
        await context.Database.OpenConnectionAsync(cancellationToken);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int> CountForeignKeyViolationsAsync(
        DbContext context,
        CancellationToken cancellationToken)
    {
        await using System.Data.Common.DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await context.Database.OpenConnectionAsync(cancellationToken);
        await using System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        int count = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            count++;
        }

        return count;
    }

    private static async Task ExecuteAsync(
        DbContext context,
        string sql,
        CancellationToken cancellationToken)
    {
        await using System.Data.Common.DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await context.Database.OpenConnectionAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            string path = databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

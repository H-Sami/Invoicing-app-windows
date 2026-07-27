using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MHC.Invoicing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    updated_at_utc_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "catalog_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name_arabic = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    name_english = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    search_name_arabic = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    search_name_english = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    sku = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    search_sku = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    unit = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    default_unit_price_halalah = table.Column<long>(type: "INTEGER", nullable: false),
                    vat_category = table.Column<int>(type: "INTEGER", nullable: false),
                    is_archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_items", x => x.id);
                    table.CheckConstraint("ck_catalog_items_archived", "is_archived IN (0, 1)");
                    table.CheckConstraint("ck_catalog_items_price", "default_unit_price_halalah >= 0");
                    table.CheckConstraint("ck_catalog_items_revision", "revision >= 0");
                    table.CheckConstraint("ck_catalog_items_updated", "updated_at_utc_ms >= created_at_utc_ms");
                    table.CheckConstraint("ck_catalog_items_vat", "vat_category IN (1, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "company_profiles",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    name_arabic = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    name_english = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    vat_number = table.Column<string>(type: "TEXT", maxLength: 15, nullable: false),
                    commercial_registration = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    branch = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    address = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    operator_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    default_payment_method = table.Column<int>(type: "INTEGER", nullable: false),
                    logo_bytes = table.Column<byte[]>(type: "BLOB", nullable: true),
                    logo_mime_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    created_at_utc_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_profiles", x => x.id);
                    table.CheckConstraint("ck_company_profiles_revision", "revision >= 0");
                    table.CheckConstraint("ck_company_profiles_singleton", "id = 1");
                    table.CheckConstraint("ck_company_profiles_updated", "updated_at_utc_ms >= created_at_utc_ms");
                });

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name_arabic = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    name_english = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    search_name_arabic = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    search_name_english = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    vat_number = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    commercial_registration = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    address = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    phone = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    is_archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.id);
                    table.CheckConstraint("ck_customers_archived", "is_archived IN (0, 1)");
                    table.CheckConstraint("ck_customers_revision", "revision >= 0");
                    table.CheckConstraint("ck_customers_updated", "updated_at_utc_ms >= created_at_utc_ms");
                });

            migrationBuilder.CreateTable(
                name: "invoice_sequences",
                columns: table => new
                {
                    issuance_year = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    next_value = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_sequences", x => x.issuance_year);
                    table.CheckConstraint("ck_invoice_sequences_next", "next_value >= 100");
                    table.CheckConstraint("ck_invoice_sequences_year", "issuance_year BETWEEN 2000 AND 9999");
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    issuance_year = table.Column<int>(type: "INTEGER", nullable: false),
                    sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    public_number = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    document_type = table.Column<int>(type: "INTEGER", nullable: false),
                    original_invoice_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    source_customer_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    business_date = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    issued_at_utc_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    issued_at_saudi_local = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    issued_saudi_offset_minutes = table.Column<int>(type: "INTEGER", nullable: false),
                    seller_name_arabic = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    seller_name_english = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    seller_vat_number = table.Column<string>(type: "TEXT", maxLength: 15, nullable: false),
                    seller_commercial_registration = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    seller_branch = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    seller_address = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    seller_logo_bytes = table.Column<byte[]>(type: "BLOB", nullable: true),
                    seller_logo_mime_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    operator_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    customer_name_arabic = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    customer_name_english = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    customer_search_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    customer_vat_number = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    customer_commercial_registration = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    customer_address = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    payment_method = table.Column<int>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    show_notes = table.Column<bool>(type: "INTEGER", nullable: false),
                    currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    subtotal_halalah = table.Column<long>(type: "INTEGER", nullable: false),
                    vat_halalah = table.Column<long>(type: "INTEGER", nullable: false),
                    grand_total_halalah = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.id);
                    table.CheckConstraint("ck_invoices_currency", "currency = 'SAR'");
                    table.CheckConstraint("ck_invoices_offset", "issued_saudi_offset_minutes = 180");
                    table.CheckConstraint("ck_invoices_original", "(document_type = 1 AND original_invoice_id IS NULL) OR (document_type = 2 AND original_invoice_id IS NOT NULL)");
                    table.CheckConstraint("ck_invoices_sequence", "sequence >= 100");
                    table.CheckConstraint("ck_invoices_totals", "grand_total_halalah = subtotal_halalah + vat_halalah");
                    table.CheckConstraint("ck_invoices_type", "document_type IN (1, 2)");
                    table.CheckConstraint("ck_invoices_year", "issuance_year BETWEEN 2000 AND 9999");
                    table.ForeignKey(
                        name: "FK_invoices_customers_source_customer_id",
                        column: x => x.source_customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoices_invoices_original_invoice_id",
                        column: x => x.original_invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    invoice_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    event_type = table.Column<int>(type: "INTEGER", nullable: false),
                    occurred_at_utc_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    operator_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    details_json = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_audit_events_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_documents",
                columns: table => new
                {
                    invoice_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    pdf_bytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    sha256 = table.Column<byte[]>(type: "BLOB", nullable: false),
                    byte_length = table.Column<long>(type: "INTEGER", nullable: false),
                    mime_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    created_at_utc_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_documents", x => x.invoice_id);
                    table.CheckConstraint("ck_invoice_documents_hash", "length(sha256) = 32");
                    table.CheckConstraint("ck_invoice_documents_length", "byte_length > 0 AND byte_length = length(pdf_bytes)");
                    table.CheckConstraint("ck_invoice_documents_mime", "mime_type = 'application/pdf'");
                    table.CheckConstraint("ck_invoice_documents_pdf", "length(pdf_bytes) > 0");
                    table.ForeignKey(
                        name: "FK_invoice_documents_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_drafts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    revision = table.Column<int>(type: "INTEGER", nullable: false),
                    document_type = table.Column<int>(type: "INTEGER", nullable: false),
                    original_invoice_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    customer_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    business_date = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    customer_name_arabic = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    customer_name_english = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    customer_vat_number = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    customer_commercial_registration = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    customer_address = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    payment_method = table.Column<int>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    show_notes = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at_utc_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_utc_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_drafts", x => x.id);
                    table.CheckConstraint("ck_invoice_drafts_original", "(document_type = 1 AND original_invoice_id IS NULL) OR (document_type = 2 AND original_invoice_id IS NOT NULL)");
                    table.CheckConstraint("ck_invoice_drafts_revision", "revision >= 0");
                    table.CheckConstraint("ck_invoice_drafts_type", "document_type IN (1, 2)");
                    table.CheckConstraint("ck_invoice_drafts_updated", "updated_at_utc_ms >= created_at_utc_ms");
                    table.ForeignKey(
                        name: "FK_invoice_drafts_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoice_drafts_invoices_original_invoice_id",
                        column: x => x.original_invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    invoice_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    source_catalog_item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    original_invoice_line_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    sku = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    unit = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    quantity_milliunits = table.Column<long>(type: "INTEGER", nullable: false),
                    unit_price_halalah = table.Column<long>(type: "INTEGER", nullable: false),
                    vat_category = table.Column<int>(type: "INTEGER", nullable: false),
                    tax_exemption_reason_code = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    tax_exemption_reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    net_halalah = table.Column<long>(type: "INTEGER", nullable: false),
                    vat_halalah = table.Column<long>(type: "INTEGER", nullable: false),
                    gross_halalah = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_lines", x => x.id);
                    table.CheckConstraint("ck_invoice_lines_gross", "gross_halalah = net_halalah + vat_halalah");
                    table.CheckConstraint("ck_invoice_lines_net", "net_halalah >= 0");
                    table.CheckConstraint("ck_invoice_lines_position", "position >= 0");
                    table.CheckConstraint("ck_invoice_lines_price", "unit_price_halalah >= 0");
                    table.CheckConstraint("ck_invoice_lines_quantity", "quantity_milliunits BETWEEN 1 AND 1000000000");
                    table.CheckConstraint("ck_invoice_lines_vat", "vat_halalah >= 0");
                    table.CheckConstraint("ck_invoice_lines_vat_category", "vat_category IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_invoice_lines_catalog_items_source_catalog_item_id",
                        column: x => x.source_catalog_item_id,
                        principalTable: "catalog_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoice_lines_invoice_lines_original_invoice_line_id",
                        column: x => x.original_invoice_line_id,
                        principalTable: "invoice_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoice_lines_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_voids",
                columns: table => new
                {
                    invoice_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    voided_at_utc_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    operator_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_voids", x => x.invoice_id);
                    table.ForeignKey(
                        name: "FK_invoice_voids_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_draft_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    draft_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    catalog_item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    original_invoice_line_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    sku = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    unit = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    quantity_milliunits = table.Column<long>(type: "INTEGER", nullable: false),
                    unit_price_halalah = table.Column<long>(type: "INTEGER", nullable: false),
                    vat_category = table.Column<int>(type: "INTEGER", nullable: false),
                    tax_exemption_reason_code = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    tax_exemption_reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_draft_lines", x => x.id);
                    table.CheckConstraint("ck_invoice_draft_lines_position", "position >= 0");
                    table.CheckConstraint("ck_invoice_draft_lines_price", "unit_price_halalah >= 0");
                    table.CheckConstraint("ck_invoice_draft_lines_quantity", "quantity_milliunits BETWEEN 1 AND 1000000000");
                    table.CheckConstraint("ck_invoice_draft_lines_vat", "vat_category IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_invoice_draft_lines_catalog_items_catalog_item_id",
                        column: x => x.catalog_item_id,
                        principalTable: "catalog_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoice_draft_lines_invoice_drafts_draft_id",
                        column: x => x.draft_id,
                        principalTable: "invoice_drafts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_invoice_draft_lines_invoice_lines_original_invoice_line_id",
                        column: x => x.original_invoice_line_id,
                        principalTable: "invoice_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_invoice_id_occurred_at_utc_ms",
                table: "audit_events",
                columns: new[] { "invoice_id", "occurred_at_utc_ms" },
                filter: "invoice_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_occurred_at_utc_ms_id",
                table: "audit_events",
                columns: new[] { "occurred_at_utc_ms", "id" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_items_is_archived_search_name_arabic_id",
                table: "catalog_items",
                columns: new[] { "is_archived", "search_name_arabic", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_items_is_archived_search_name_english_id",
                table: "catalog_items",
                columns: new[] { "is_archived", "search_name_english", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_items_is_archived_search_sku_id",
                table: "catalog_items",
                columns: new[] { "is_archived", "search_sku", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_items_search_sku",
                table: "catalog_items",
                column: "search_sku",
                unique: true,
                filter: "is_archived = 0 AND search_sku <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_customers_commercial_registration",
                table: "customers",
                column: "commercial_registration",
                filter: "commercial_registration IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_customers_is_archived_search_name_arabic_id",
                table: "customers",
                columns: new[] { "is_archived", "search_name_arabic", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_customers_is_archived_search_name_english_id",
                table: "customers",
                columns: new[] { "is_archived", "search_name_english", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_customers_vat_number",
                table: "customers",
                column: "vat_number",
                filter: "vat_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_draft_lines_catalog_item_id",
                table: "invoice_draft_lines",
                column: "catalog_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_draft_lines_draft_id_position",
                table: "invoice_draft_lines",
                columns: new[] { "draft_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoice_draft_lines_original_invoice_line_id",
                table: "invoice_draft_lines",
                column: "original_invoice_line_id",
                filter: "original_invoice_line_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_drafts_customer_id",
                table: "invoice_drafts",
                column: "customer_id",
                filter: "customer_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_drafts_original_invoice_id",
                table: "invoice_drafts",
                column: "original_invoice_id",
                filter: "original_invoice_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_drafts_updated_at_utc_ms_id",
                table: "invoice_drafts",
                columns: new[] { "updated_at_utc_ms", "id" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_invoice_id_position",
                table: "invoice_lines",
                columns: new[] { "invoice_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_original_invoice_line_id",
                table: "invoice_lines",
                column: "original_invoice_line_id",
                filter: "original_invoice_line_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_source_catalog_item_id",
                table: "invoice_lines",
                column: "source_catalog_item_id");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_business_date_id",
                table: "invoices",
                columns: new[] { "business_date", "id" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "IX_invoices_customer_commercial_registration",
                table: "invoices",
                column: "customer_commercial_registration",
                filter: "customer_commercial_registration IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_customer_search_name_id",
                table: "invoices",
                columns: new[] { "customer_search_name", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_invoices_customer_vat_number",
                table: "invoices",
                column: "customer_vat_number",
                filter: "customer_vat_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_document_type_issued_at_utc_ms",
                table: "invoices",
                columns: new[] { "document_type", "issued_at_utc_ms" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_invoices_issuance_year_sequence",
                table: "invoices",
                columns: new[] { "issuance_year", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_issued_at_utc_ms_id",
                table: "invoices",
                columns: new[] { "issued_at_utc_ms", "id" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "IX_invoices_original_invoice_id",
                table: "invoices",
                column: "original_invoice_id",
                filter: "original_invoice_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_public_number",
                table: "invoices",
                column: "public_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_source_customer_id",
                table: "invoices",
                column: "source_customer_id");

            migrationBuilder.Sql(
                """
                CREATE TABLE invoice_finalizations (
                    invoice_id TEXT NOT NULL CONSTRAINT PK_invoice_finalizations PRIMARY KEY,
                    finalized_at_utc_ms INTEGER NOT NULL,
                    CONSTRAINT FK_invoice_finalizations_invoices_invoice_id
                        FOREIGN KEY (invoice_id) REFERENCES invoices (id) ON DELETE RESTRICT
                );
                """);
            migrationBuilder.Sql(
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
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_invoice_lines_no_insert_after_finalization
                BEFORE INSERT ON invoice_lines
                WHEN EXISTS (SELECT 1 FROM invoice_finalizations WHERE invoice_id = NEW.invoice_id)
                BEGIN
                    SELECT RAISE(ABORT, 'finalized invoice lines cannot be extended');
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_invoice_documents_no_insert_after_finalization
                BEFORE INSERT ON invoice_documents
                WHEN EXISTS (SELECT 1 FROM invoice_finalizations WHERE invoice_id = NEW.invoice_id)
                BEGIN
                    SELECT RAISE(ABORT, 'finalized invoice documents cannot be added');
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_invoice_finalizations_no_update
                BEFORE UPDATE ON invoice_finalizations
                BEGIN
                    SELECT RAISE(ABORT, 'invoice finalization records are immutable');
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_invoice_finalizations_no_delete
                BEFORE DELETE ON invoice_finalizations
                BEGIN
                    SELECT RAISE(ABORT, 'invoice finalization records cannot be deleted');
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_invoices_no_update
                BEFORE UPDATE ON invoices
                BEGIN
                    SELECT RAISE(ABORT, 'issued invoices are immutable');
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_invoices_no_delete
                BEFORE DELETE ON invoices
                BEGIN
                    SELECT RAISE(ABORT, 'issued invoices cannot be deleted');
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_invoice_lines_no_update
                BEFORE UPDATE ON invoice_lines
                BEGIN
                    SELECT RAISE(ABORT, 'issued invoice lines are immutable');
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_invoice_lines_no_delete
                BEFORE DELETE ON invoice_lines
                BEGIN
                    SELECT RAISE(ABORT, 'issued invoice lines cannot be deleted');
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_invoice_documents_no_update
                BEFORE UPDATE ON invoice_documents
                BEGIN
                    SELECT RAISE(ABORT, 'issued documents are immutable');
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_invoice_documents_no_delete
                BEFORE DELETE ON invoice_documents
                BEGIN
                    SELECT RAISE(ABORT, 'issued documents cannot be deleted');
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_invoice_voids_no_update
                BEFORE UPDATE ON invoice_voids
                BEGIN
                    SELECT RAISE(ABORT, 'invoice void records are immutable');
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_invoice_voids_no_delete
                BEFORE DELETE ON invoice_voids
                BEGIN
                    SELECT RAISE(ABORT, 'invoice void records cannot be deleted');
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_audit_events_no_update
                BEFORE UPDATE ON audit_events
                BEGIN
                    SELECT RAISE(ABORT, 'audit events are immutable');
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER trg_audit_events_no_delete
                BEFORE DELETE ON audit_events
                BEGIN
                    SELECT RAISE(ABORT, 'audit events cannot be deleted');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_settings");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "company_profiles");

            migrationBuilder.Sql("DROP TABLE invoice_finalizations;");

            migrationBuilder.DropTable(
                name: "invoice_documents");

            migrationBuilder.DropTable(
                name: "invoice_draft_lines");

            migrationBuilder.DropTable(
                name: "invoice_sequences");

            migrationBuilder.DropTable(
                name: "invoice_voids");

            migrationBuilder.DropTable(
                name: "invoice_drafts");

            migrationBuilder.DropTable(
                name: "invoice_lines");

            migrationBuilder.DropTable(
                name: "catalog_items");

            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropTable(
                name: "customers");
        }
    }
}

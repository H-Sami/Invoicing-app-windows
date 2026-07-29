using MHC.Invoicing.Domain.Validation;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MHC.Invoicing.Infrastructure.Persistence.Configurations;

internal sealed class CompanyProfileConfiguration : IEntityTypeConfiguration<CompanyProfileEntity>
{
    public void Configure(EntityTypeBuilder<CompanyProfileEntity> builder)
    {
        builder.ToTable("company_profiles", table =>
        {
            table.HasCheckConstraint("ck_company_profiles_singleton", "id = 1");
            table.HasCheckConstraint("ck_company_profiles_revision", "revision >= 0");
            table.HasCheckConstraint("ck_company_profiles_updated", "updated_at_utc_ms >= created_at_utc_ms");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.NameArabic).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.NameEnglish).HasMaxLength(DomainFieldLimits.PartyName);
        builder.Property(entity => entity.VatNumber).HasMaxLength(DomainFieldLimits.TaxIdentifier).IsRequired();
        builder.Property(entity => entity.CommercialRegistration).HasMaxLength(DomainFieldLimits.CommercialRegistration);
        builder.Property(entity => entity.Branch).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.Address).HasMaxLength(DomainFieldLimits.Address).IsRequired();
        builder.Property(entity => entity.OperatorName).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.LogoMimeType).HasMaxLength(32);
        builder.Property(entity => entity.Revision).IsConcurrencyToken();
    }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<CustomerEntity>
{
    public void Configure(EntityTypeBuilder<CustomerEntity> builder)
    {
        builder.ToTable("customers", table =>
        {
            table.HasCheckConstraint("ck_customers_revision", "revision >= 0");
            table.HasCheckConstraint("ck_customers_archived", "is_archived IN (0, 1)");
            table.HasCheckConstraint("ck_customers_updated", "updated_at_utc_ms >= created_at_utc_ms");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnType("TEXT");
        builder.Property(entity => entity.NameArabic).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.NameEnglish).HasMaxLength(DomainFieldLimits.PartyName);
        builder.Property(entity => entity.SearchNameArabic).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.SearchNameEnglish).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.VatNumber).HasMaxLength(DomainFieldLimits.TaxIdentifier);
        builder.Property(entity => entity.CommercialRegistration).HasMaxLength(DomainFieldLimits.CommercialRegistration);
        builder.Property(entity => entity.Address).HasMaxLength(DomainFieldLimits.Address);
        builder.Property(entity => entity.Phone).HasMaxLength(DomainFieldLimits.Phone);
        builder.Property(entity => entity.Email).HasMaxLength(DomainFieldLimits.Email);
        builder.Property(entity => entity.Revision).IsConcurrencyToken();
        builder.HasIndex(entity => new { entity.IsArchived, entity.SearchNameArabic, entity.Id });
        builder.HasIndex(entity => new { entity.IsArchived, entity.SearchNameEnglish, entity.Id });
        builder.HasIndex(entity => entity.VatNumber).HasFilter("vat_number IS NOT NULL");
        builder.HasIndex(entity => entity.CommercialRegistration).HasFilter("commercial_registration IS NOT NULL");
    }
}

internal sealed class CatalogItemConfiguration : IEntityTypeConfiguration<CatalogItemEntity>
{
    public void Configure(EntityTypeBuilder<CatalogItemEntity> builder)
    {
        builder.ToTable("catalog_items", table =>
        {
            table.HasCheckConstraint("ck_catalog_items_price", "default_unit_price_halalah >= 0");
            table.HasCheckConstraint("ck_catalog_items_vat", "vat_category IN (1, 2, 3)");
            table.HasCheckConstraint("ck_catalog_items_archived", "is_archived IN (0, 1)");
            table.HasCheckConstraint("ck_catalog_items_revision", "revision >= 0");
            table.HasCheckConstraint("ck_catalog_items_updated", "updated_at_utc_ms >= created_at_utc_ms");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnType("TEXT");
        builder.Property(entity => entity.NameArabic).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.NameEnglish).HasMaxLength(DomainFieldLimits.PartyName);
        builder.Property(entity => entity.SearchNameArabic).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.SearchNameEnglish).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.Sku).HasMaxLength(DomainFieldLimits.Sku);
        builder.Property(entity => entity.SearchSku).HasMaxLength(DomainFieldLimits.Sku).IsRequired();
        builder.Property(entity => entity.Unit).HasMaxLength(DomainFieldLimits.Unit).IsRequired();
        builder.Property(entity => entity.DefaultUnitPriceHalalah).HasColumnType("INTEGER");
        builder.Property(entity => entity.Revision).IsConcurrencyToken();
        builder.HasIndex(entity => new { entity.IsArchived, entity.SearchNameArabic, entity.Id });
        builder.HasIndex(entity => new { entity.IsArchived, entity.SearchNameEnglish, entity.Id });
        builder.HasIndex(entity => new { entity.IsArchived, entity.SearchSku, entity.Id });
        builder.HasIndex(entity => entity.SearchSku)
            .IsUnique()
            .HasFilter("is_archived = 0 AND search_sku <> ''");
    }
}

internal sealed class InvoiceDraftConfiguration : IEntityTypeConfiguration<InvoiceDraftEntity>
{
    public void Configure(EntityTypeBuilder<InvoiceDraftEntity> builder)
    {
        builder.ToTable("invoice_drafts", table =>
        {
            table.HasCheckConstraint("ck_invoice_drafts_revision", "revision >= 0");
            table.HasCheckConstraint("ck_invoice_drafts_type", "document_type IN (1, 2)");
            table.HasCheckConstraint("ck_invoice_drafts_updated", "updated_at_utc_ms >= created_at_utc_ms");
            table.HasCheckConstraint(
                "ck_invoice_drafts_original",
                "(document_type = 1 AND original_invoice_id IS NULL) OR (document_type = 2 AND original_invoice_id IS NOT NULL)");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnType("TEXT");
        builder.Property(entity => entity.BusinessDate).HasMaxLength(10).IsRequired();
        builder.Property(entity => entity.CustomerNameArabic).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.CustomerNameEnglish).HasMaxLength(DomainFieldLimits.PartyName);
        builder.Property(entity => entity.CustomerVatNumber).HasMaxLength(DomainFieldLimits.TaxIdentifier);
        builder.Property(entity => entity.CustomerCommercialRegistration).HasMaxLength(DomainFieldLimits.CommercialRegistration);
        builder.Property(entity => entity.CustomerAddress).HasMaxLength(DomainFieldLimits.Address);
        builder.Property(entity => entity.Title).HasMaxLength(DomainFieldLimits.Title);
        builder.Property(entity => entity.Notes).HasMaxLength(DomainFieldLimits.Notes);
        builder.Property(entity => entity.Revision).IsConcurrencyToken();
        builder.HasOne<InvoiceEntity>().WithMany().HasForeignKey(entity => entity.OriginalInvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CustomerEntity>().WithMany().HasForeignKey(entity => entity.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(entity => entity.Lines).WithOne(entity => entity.Draft).HasForeignKey(entity => entity.DraftId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(entity => new { entity.UpdatedAtUtcMs, entity.Id }).IsDescending(true, false);
        builder.HasIndex(entity => entity.CustomerId).HasFilter("customer_id IS NOT NULL");
        builder.HasIndex(entity => entity.OriginalInvoiceId).HasFilter("original_invoice_id IS NOT NULL");
    }
}

internal sealed class InvoiceDraftLineConfiguration : IEntityTypeConfiguration<InvoiceDraftLineEntity>
{
    public void Configure(EntityTypeBuilder<InvoiceDraftLineEntity> builder)
    {
        builder.ToTable("invoice_draft_lines", table =>
        {
            table.HasCheckConstraint("ck_invoice_draft_lines_position", "position >= 0");
            table.HasCheckConstraint("ck_invoice_draft_lines_quantity", "quantity_milliunits BETWEEN 1 AND 1000000000");
            table.HasCheckConstraint("ck_invoice_draft_lines_price", "unit_price_halalah >= 0");
            table.HasCheckConstraint("ck_invoice_draft_lines_vat", "vat_category IN (1, 2, 3)");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnType("TEXT");
        builder.Property(entity => entity.Description).HasMaxLength(DomainFieldLimits.LineDescription).IsRequired();
        builder.Property(entity => entity.Sku).HasMaxLength(DomainFieldLimits.Sku);
        builder.Property(entity => entity.Unit).HasMaxLength(DomainFieldLimits.Unit).IsRequired();
        builder.Property(entity => entity.QuantityMilliunits).HasColumnType("INTEGER");
        builder.Property(entity => entity.UnitPriceHalalah).HasColumnType("INTEGER");
        builder.Property(entity => entity.TaxExemptionReasonCode).HasMaxLength(DomainFieldLimits.TaxExemptionReasonCode);
        builder.Property(entity => entity.TaxExemptionReason).HasMaxLength(DomainFieldLimits.LineDescription);
        builder.HasOne<CatalogItemEntity>().WithMany().HasForeignKey(entity => entity.CatalogItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<InvoiceLineEntity>().WithMany().HasForeignKey(entity => entity.OriginalInvoiceLineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => new { entity.DraftId, entity.Position }).IsUnique();
        builder.HasIndex(entity => entity.OriginalInvoiceLineId).HasFilter("original_invoice_line_id IS NOT NULL");
    }
}

internal sealed class InvoiceSequenceConfiguration : IEntityTypeConfiguration<InvoiceSequenceEntity>
{
    public void Configure(EntityTypeBuilder<InvoiceSequenceEntity> builder)
    {
        builder.ToTable("invoice_sequences", table =>
        {
            table.HasCheckConstraint("ck_invoice_sequences_year", "issuance_year BETWEEN 2000 AND 9999");
            table.HasCheckConstraint("ck_invoice_sequences_next", "next_value >= 100");
        });
        builder.HasKey(entity => entity.IssuanceYear);
    }
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<InvoiceEntity>
{
    public void Configure(EntityTypeBuilder<InvoiceEntity> builder)
    {
        builder.ToTable("invoices", table =>
        {
            table.HasCheckConstraint("ck_invoices_year", "issuance_year BETWEEN 2000 AND 9999");
            table.HasCheckConstraint("ck_invoices_sequence", "sequence >= 100");
            table.HasCheckConstraint("ck_invoices_type", "document_type IN (1, 2)");
            table.HasCheckConstraint("ck_invoices_offset", "issued_saudi_offset_minutes = 180");
            table.HasCheckConstraint("ck_invoices_currency", "currency = 'SAR'");
            table.HasCheckConstraint("ck_invoices_totals", "grand_total_halalah = subtotal_halalah + vat_halalah");
            table.HasCheckConstraint(
                "ck_invoices_original",
                "(document_type = 1 AND original_invoice_id IS NULL) OR (document_type = 2 AND original_invoice_id IS NOT NULL)");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnType("TEXT");
        builder.Property(entity => entity.PublicNumber).HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.BusinessDate).HasMaxLength(10).IsRequired();
        builder.Property(entity => entity.IssuedAtSaudiLocal).HasMaxLength(40).IsRequired();
        builder.Property(entity => entity.SellerNameArabic).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.SellerNameEnglish).HasMaxLength(DomainFieldLimits.PartyName);
        builder.Property(entity => entity.SellerVatNumber).HasMaxLength(DomainFieldLimits.TaxIdentifier).IsRequired();
        builder.Property(entity => entity.SellerCommercialRegistration).HasMaxLength(DomainFieldLimits.CommercialRegistration);
        builder.Property(entity => entity.SellerBranch).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.SellerAddress).HasMaxLength(DomainFieldLimits.Address).IsRequired();
        builder.Property(entity => entity.SellerLogoMimeType).HasMaxLength(32);
        builder.Property(entity => entity.OperatorName).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.CustomerNameArabic).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.CustomerNameEnglish).HasMaxLength(DomainFieldLimits.PartyName);
        builder.Property(entity => entity.CustomerSearchName).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.Property(entity => entity.CustomerVatNumber).HasMaxLength(DomainFieldLimits.TaxIdentifier);
        builder.Property(entity => entity.CustomerCommercialRegistration).HasMaxLength(DomainFieldLimits.CommercialRegistration);
        builder.Property(entity => entity.CustomerAddress).HasMaxLength(DomainFieldLimits.Address);
        builder.Property(entity => entity.Title).HasMaxLength(DomainFieldLimits.Title);
        builder.Property(entity => entity.Notes).HasMaxLength(DomainFieldLimits.Notes);
        builder.Property(entity => entity.Currency).HasMaxLength(3).IsRequired();
        builder.Property(entity => entity.SubtotalHalalah).HasColumnType("INTEGER");
        builder.Property(entity => entity.VatHalalah).HasColumnType("INTEGER");
        builder.Property(entity => entity.GrandTotalHalalah).HasColumnType("INTEGER");
        builder.HasOne<InvoiceEntity>().WithMany().HasForeignKey(entity => entity.OriginalInvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CustomerEntity>().WithMany().HasForeignKey(entity => entity.SourceCustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(entity => entity.Lines).WithOne(entity => entity.Invoice).HasForeignKey(entity => entity.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => entity.PublicNumber).IsUnique();
        builder.HasIndex(entity => new { entity.IssuanceYear, entity.Sequence }).IsUnique();
        builder.HasIndex(entity => new { entity.IssuedAtUtcMs, entity.Id }).IsDescending(true, false);
        builder.HasIndex(entity => new { entity.BusinessDate, entity.Id }).IsDescending(true, false);
        builder.HasIndex(entity => new { entity.CustomerSearchName, entity.Id });
        builder.HasIndex(entity => entity.CustomerVatNumber).HasFilter("customer_vat_number IS NOT NULL");
        builder.HasIndex(entity => entity.CustomerCommercialRegistration).HasFilter("customer_commercial_registration IS NOT NULL");
        builder.HasIndex(entity => entity.OriginalInvoiceId).HasFilter("original_invoice_id IS NOT NULL");
        builder.HasIndex(entity => new { entity.DocumentType, entity.IssuedAtUtcMs }).IsDescending(false, true);
    }
}

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLineEntity>
{
    public void Configure(EntityTypeBuilder<InvoiceLineEntity> builder)
    {
        builder.ToTable("invoice_lines", table =>
        {
            table.HasCheckConstraint("ck_invoice_lines_position", "position >= 0");
            table.HasCheckConstraint("ck_invoice_lines_quantity", "quantity_milliunits BETWEEN 1 AND 1000000000");
            table.HasCheckConstraint("ck_invoice_lines_price", "unit_price_halalah >= 0");
            table.HasCheckConstraint("ck_invoice_lines_vat_category", "vat_category IN (1, 2, 3)");
            table.HasCheckConstraint("ck_invoice_lines_net", "net_halalah >= 0");
            table.HasCheckConstraint("ck_invoice_lines_vat", "vat_halalah >= 0");
            table.HasCheckConstraint("ck_invoice_lines_gross", "gross_halalah = net_halalah + vat_halalah");
        });
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnType("TEXT");
        builder.Property(entity => entity.Description).HasMaxLength(DomainFieldLimits.LineDescription).IsRequired();
        builder.Property(entity => entity.Sku).HasMaxLength(DomainFieldLimits.Sku);
        builder.Property(entity => entity.Unit).HasMaxLength(DomainFieldLimits.Unit).IsRequired();
        builder.Property(entity => entity.QuantityMilliunits).HasColumnType("INTEGER");
        builder.Property(entity => entity.UnitPriceHalalah).HasColumnType("INTEGER");
        builder.Property(entity => entity.TaxExemptionReasonCode).HasMaxLength(DomainFieldLimits.TaxExemptionReasonCode);
        builder.Property(entity => entity.TaxExemptionReason).HasMaxLength(DomainFieldLimits.LineDescription);
        builder.Property(entity => entity.NetHalalah).HasColumnType("INTEGER");
        builder.Property(entity => entity.VatHalalah).HasColumnType("INTEGER");
        builder.Property(entity => entity.GrossHalalah).HasColumnType("INTEGER");
        builder.HasOne<CatalogItemEntity>().WithMany().HasForeignKey(entity => entity.SourceCatalogItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<InvoiceLineEntity>().WithMany().HasForeignKey(entity => entity.OriginalInvoiceLineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => new { entity.InvoiceId, entity.Position }).IsUnique();
        builder.HasIndex(entity => entity.OriginalInvoiceLineId).HasFilter("original_invoice_line_id IS NOT NULL");
    }
}

internal sealed class InvoiceDocumentConfiguration : IEntityTypeConfiguration<InvoiceDocumentEntity>
{
    public void Configure(EntityTypeBuilder<InvoiceDocumentEntity> builder)
    {
        builder.ToTable("invoice_documents", table =>
        {
            table.HasCheckConstraint("ck_invoice_documents_pdf", "length(pdf_bytes) > 0");
            table.HasCheckConstraint("ck_invoice_documents_hash", "length(sha256) = 32");
            table.HasCheckConstraint("ck_invoice_documents_length", "byte_length > 0 AND byte_length = length(pdf_bytes)");
            table.HasCheckConstraint("ck_invoice_documents_mime", "mime_type = 'application/pdf'");
        });
        builder.HasKey(entity => entity.InvoiceId);
        builder.Property(entity => entity.InvoiceId).HasColumnType("TEXT");
        builder.Property(entity => entity.MimeType).HasMaxLength(32).IsRequired();
        builder.HasOne(entity => entity.Invoice).WithOne(entity => entity.Document).HasForeignKey<InvoiceDocumentEntity>(entity => entity.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InvoiceVoidConfiguration : IEntityTypeConfiguration<InvoiceVoidEntity>
{
    public void Configure(EntityTypeBuilder<InvoiceVoidEntity> builder)
    {
        builder.ToTable("invoice_voids");
        builder.HasKey(entity => entity.InvoiceId);
        builder.Property(entity => entity.InvoiceId).HasColumnType("TEXT");
        builder.Property(entity => entity.Reason).HasMaxLength(1_000).IsRequired();
        builder.Property(entity => entity.OperatorName).HasMaxLength(DomainFieldLimits.PartyName).IsRequired();
        builder.HasOne(entity => entity.Invoice).WithOne(entity => entity.Void).HasForeignKey<InvoiceVoidEntity>(entity => entity.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEventEntity>
{
    public void Configure(EntityTypeBuilder<AuditEventEntity> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnType("TEXT");
        builder.Property(entity => entity.OperatorName).HasMaxLength(DomainFieldLimits.PartyName);
        builder.Property(entity => entity.DetailsJson).HasMaxLength(4_000);
        builder.HasOne<InvoiceEntity>().WithMany().HasForeignKey(entity => entity.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => new { entity.OccurredAtUtcMs, entity.Id }).IsDescending(true, false);
        builder.HasIndex(entity => new { entity.InvoiceId, entity.OccurredAtUtcMs }).HasFilter("invoice_id IS NOT NULL");
    }
}

internal sealed class AppSettingConfiguration : IEntityTypeConfiguration<AppSettingEntity>
{
    public void Configure(EntityTypeBuilder<AppSettingEntity> builder)
    {
        builder.ToTable("app_settings");
        builder.HasKey(entity => entity.Key);
        builder.Property(entity => entity.Key).HasMaxLength(100);
        builder.Property(entity => entity.Value).HasMaxLength(4_000).IsRequired();
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MHC.Invoicing.Application.Abstractions;
using MHC.Invoicing.Application.Documents;
using MHC.Invoicing.Application.Drafts;
using MHC.Invoicing.Application.Issuance;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.Search;
using MHC.Invoicing.Domain.Time;
using MHC.Invoicing.Domain.ValueObjects;
using MHC.Invoicing.Infrastructure.Documents;
using MHC.Invoicing.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MHC.Invoicing.Infrastructure.Persistence;

public sealed class InvoiceIssuanceService(
    string connectionString,
    IClock clock,
    IDocumentSerialGenerator serialGenerator,
    IInvoiceHtmlRenderer htmlRenderer,
    IInvoicePdfRenderer pdfRenderer,
    IZatcaQrGenerator qrGenerator)
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("A SQLite connection string is required.", nameof(connectionString))
        : connectionString;

    public Task<IssuedInvoice> IssueSaleAsync(
        IssueSaleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IssueAsync(request.DraftId, request.ExpectedDraftRevision, InvoiceDocumentType.TaxInvoice, cancellationToken);
    }

    public Task<IssuedInvoice> IssueCreditNoteAsync(
        IssueCreditNoteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IssueAsync(request.DraftId, request.ExpectedDraftRevision, InvoiceDocumentType.CreditNote, cancellationToken);
    }

    private async Task<IssuedInvoice> IssueAsync(
        Guid draftId,
        int expectedDraftRevision,
        InvoiceDocumentType expectedType,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? authoritativeIssuedAtUtc = null;
        DocumentSerial serial = serialGenerator.Create();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using SqliteConnection connection = new(_connectionString);
            await connection.OpenAsync(cancellationToken);
            DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
                .UseSqlite(connection)
                .Options;

            PreparedIssue optimistic;
            InvoiceNumber candidate;
            await using (MhcDbContext readContext = new(options))
            {
                DraftRecord draft = await LoadDraftAsync(readContext, draftId, expectedDraftRevision, cancellationToken);
                DateTimeOffset issuedAtUtc = authoritativeIssuedAtUtc ?? clock.UtcNow;
                IssueTiming timing = IssueTiming.Capture(draft.BusinessDate, issuedAtUtc);
                candidate = await InvoiceNumberAllocator.PeekAsync(timing.IssuedAtSaudi.Year, connection, cancellationToken: cancellationToken);
                optimistic = await PrepareAsync(readContext, draft, expectedType, candidate, serial, timing, cancellationToken);
            }

            byte[] pdfBytes = await RenderPdfAsync(optimistic, cancellationToken);
            byte[] pdfHash = SHA256.HashData(pdfBytes);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            await using MhcDbContext writeContext = new(options);
            await writeContext.Database.UseTransactionAsync(transaction, cancellationToken);

            DraftRecord authoritativeDraft = await LoadDraftAsync(
                writeContext, draftId, expectedDraftRevision, cancellationToken);
            DateTimeOffset transactionIssuedAtUtc = authoritativeIssuedAtUtc ?? clock.UtcNow;
            IssueTiming authoritativeTiming = IssueTiming.Capture(authoritativeDraft.BusinessDate, transactionIssuedAtUtc);
            if (authoritativeIssuedAtUtc is null &&
                authoritativeTiming.IssuedAtUtc != optimistic.Invoice.Timing.IssuedAtUtc)
            {
                await transaction.RollbackAsync(cancellationToken);
                authoritativeIssuedAtUtc = authoritativeTiming.IssuedAtUtc;
                continue;
            }
            PreparedIssue authoritative = await PrepareAsync(
                writeContext, authoritativeDraft, expectedType, candidate, serial, authoritativeTiming, cancellationToken);
            InvoiceNumber current = await InvoiceNumberAllocator.PeekAsync(
                candidate.Year, connection, transaction, cancellationToken);

            if (current != candidate || authoritative.StateToken != optimistic.StateToken)
            {
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }

            InvoiceNumber allocated = await InvoiceNumberAllocator.AllocateWithinTransactionAsync(
                candidate.Year, connection, transaction, cancellationToken);
            if (allocated != candidate)
            {
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }

            InvoiceEntity entity = authoritative.Original is null
                ? ToSaleEntity(authoritative.Invoice, authoritative.Draft, authoritative.Company, pdfBytes, pdfHash)
                : ToCreditEntity(authoritative.Invoice, authoritative.Draft, authoritative.Original, authoritative.Company, pdfBytes, pdfHash);
            writeContext.Invoices.Add(entity);
            writeContext.AuditEvents.Add(new AuditEventEntity
            {
                Id = Guid.CreateVersion7(),
                InvoiceId = authoritative.Invoice.Id,
                EventType = expectedType == InvoiceDocumentType.TaxInvoice ? 1 : 2,
                OccurredAtUtcMs = authoritativeTiming.IssuedAtUtc.ToUnixTimeMilliseconds(),
                OperatorName = authoritative.Invoice.OperatorName,
            });
            int deleted = await writeContext.InvoiceDrafts
                .Where(draft => draft.Id == draftId && draft.Revision == expectedDraftRevision)
                .ExecuteDeleteAsync(cancellationToken);
            if (deleted != 1)
            {
                throw DraftConcurrencyFailure(draftId, expectedDraftRevision);
            }

            await writeContext.SaveChangesAsync(cancellationToken);
            await CanonicalInvoiceFinalizer.FinalizeAsync(
                writeContext,
                authoritative.Invoice.Id,
                authoritativeTiming.IssuedAtUtc.ToUnixTimeMilliseconds(),
                pdfHash,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return authoritative.Invoice;
        }
    }

    private static async Task<PreparedIssue> PrepareAsync(
        MhcDbContext context,
        DraftRecord draft,
        InvoiceDocumentType expectedType,
        InvoiceNumber number,
        DocumentSerial serial,
        IssueTiming timing,
        CancellationToken cancellationToken)
    {
        if (draft.DocumentType != expectedType)
        {
            throw new DomainValidationException($"Issuance requires a persisted {expectedType} draft.");
        }

        CompanyProfileEntity company = await context.CompanyProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(profile => profile.Id == 1, cancellationToken)
            ?? throw new DomainValidationException("A persisted company profile is required for issuance.");
        CompanySnapshot companySnapshot = ToCompanySnapshot(company);

        if (expectedType == InvoiceDocumentType.TaxInvoice)
        {
            if (draft.OriginalInvoiceId is not null)
            {
                throw new DomainValidationException("A sale draft cannot reference an original invoice.");
            }

            ValidateSale(draft, companySnapshot.Seller);
            InvoiceCalculation calculation = InvoiceCalculator.Calculate(draft.Lines.Select(ToInput).ToArray());
            IssuedInvoice sale = IssuedInvoice.CreateSale(
                number,
                serial,
                timing,
                companySnapshot.Seller,
                ToPartySnapshot(draft.Customer),
                companySnapshot.Branch,
                companySnapshot.OperatorName,
                draft.PaymentMethod.ToString(),
                draft.Title,
                draft.ShowNotes ? draft.Notes : null,
                calculation);
            return new PreparedIssue(
                sale, draft, companySnapshot, null,
                StateToken(draft, company, null, null),
                draft.ShowNotes, companySnapshot.LogoBytes, companySnapshot.LogoMimeType, null);
        }

        Guid originalId = draft.OriginalInvoiceId
            ?? throw new DomainValidationException("A credit-note draft must reference an original invoice.");
        InvoiceEntity original = await context.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Void)
            .SingleOrDefaultAsync(invoice => invoice.Id == originalId, cancellationToken)
            ?? throw new DomainValidationException("The original invoice does not exist.");
        if (original.DocumentType != InvoiceDocumentType.TaxInvoice)
        {
            throw new DomainValidationException("A credit note can reference only a tax invoice.");
        }
        if (original.Void is not null)
        {
            throw new DomainValidationException("A credit note cannot reference a voided tax invoice.");
        }

        List<InvoiceEntity> priorCredits = await context.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Where(invoice => invoice.OriginalInvoiceId == original.Id && invoice.DocumentType == InvoiceDocumentType.CreditNote)
            .OrderBy(invoice => invoice.IssuedAtUtcMs)
            .ThenBy(invoice => invoice.Id)
            .ToListAsync(cancellationToken);
        Dictionary<Guid, decimal> creditedQuantities = priorCredits
            .SelectMany(invoice => invoice.Lines)
            .Where(line => line.OriginalInvoiceLineId.HasValue)
            .GroupBy(line => line.OriginalInvoiceLineId!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.QuantityMilliunits) / 1_000m);
        IReadOnlyList<OriginalInvoiceLineCreditState> creditState = original.Lines
            .OrderBy(line => line.Position)
            .Select(line => new OriginalInvoiceLineCreditState(
                line.Id, line.QuantityMilliunits / 1_000m, creditedQuantities.GetValueOrDefault(line.Id)))
            .ToArray();
        Money alreadyCreditedGross = priorCredits.Aggregate(
            Money.Zero, (sum, credit) => sum + new Money(credit.GrandTotalHalalah));
        CreditLineRequest[] requestedLines = draft.Lines.Select(line => new CreditLineRequest(
            line.OriginalInvoiceLineId ?? throw new DomainValidationException(
                "Every credit-note draft line must reference an original invoice line."),
            line.Quantity)).ToArray();
        IssuedInvoice credit = IssuedInvoice.CreateCreditNote(
            RehydrateSale(original),
            number,
            serial,
            timing,
            companySnapshot.OperatorName,
            draft.PaymentMethod.ToString(),
            draft.Title,
            draft.ShowNotes ? draft.Notes : null,
            alreadyCreditedGross,
            creditState,
            requestedLines);
        return new PreparedIssue(
            credit, draft, companySnapshot, original,
            StateToken(draft, company, original, priorCredits),
            draft.ShowNotes, original.SellerLogoBytes, original.SellerLogoMimeType, original.PublicNumber);
    }

    private async Task<byte[]> RenderPdfAsync(PreparedIssue prepared, CancellationToken cancellationToken)
    {
        IssuedInvoice invoice = prepared.Invoice;
        ZatcaQrCode qr = qrGenerator.Generate(new ZatcaQrData(
            invoice.Seller.NameArabic,
            invoice.Seller.VatNumber ?? throw new DomainValidationException("A 15-digit seller VAT number is required for issuance."),
            invoice.Timing.IssuedAtSaudi,
            invoice.Totals.GrandTotal,
            invoice.Totals.Vat));
        InvoiceDocumentModel model = new(
            invoice.Number.ToString(), invoice.Serial.Value, invoice.Type, prepared.OriginalPublicNumber,
            invoice.Timing.BusinessDate, invoice.Timing.IssuedAtSaudi, invoice.Seller, invoice.Customer,
            invoice.Branch, invoice.OperatorName, invoice.PaymentMethod, invoice.Title, invoice.Notes,
            prepared.ShowNotes, prepared.LogoBytes?.ToArray(), prepared.LogoMimeType, qr.PngBytes,
            invoice.Lines.Select(line => new InvoiceDocumentLine(
                line.Id, line.Description, line.Sku, line.Unit, line.Quantity, line.UnitPrice,
                line.VatCategory, line.TaxExemptionReasonCode, line.TaxExemptionReason,
                line.UnitPrice.Multiply(line.Quantity) - line.Net,
                line.Net, line.Vat, line.Gross)).ToArray(),
            invoice.Totals.Subtotal, invoice.Totals.Vat, invoice.Totals.GrandTotal);
        byte[] pdfBytes = await pdfRenderer.RenderAsync(htmlRenderer.Render(model), cancellationToken);
        if (pdfBytes.Length < 9 || !pdfBytes.AsSpan().StartsWith("%PDF-"u8))
        {
            throw new InvalidDataException("The invoice renderer did not return a valid PDF payload.");
        }
        return pdfBytes;
    }

    private static async Task<DraftRecord> LoadDraftAsync(
        MhcDbContext context, Guid id, int revision, CancellationToken cancellationToken)
    {
        InvoiceDraftEntity entity = await context.InvoiceDrafts
            .AsNoTracking()
            .Include(draft => draft.Lines)
            .SingleOrDefaultAsync(draft => draft.Id == id && draft.Revision == revision, cancellationToken)
            ?? throw DraftConcurrencyFailure(id, revision);
        return ToDraft(entity);
    }

    private static DraftRecord ToDraft(InvoiceDraftEntity entity) => new(
        entity.Id, entity.DocumentType, entity.OriginalInvoiceId, entity.CustomerId,
        DateOnly.ParseExact(entity.BusinessDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        new DraftParty(entity.CustomerNameArabic, entity.CustomerNameEnglish, entity.CustomerVatNumber,
            entity.CustomerCommercialRegistration, entity.CustomerAddress),
        entity.PaymentMethod, entity.Title, entity.Notes, entity.ShowNotes,
        entity.Lines.OrderBy(line => line.Position).Select(line => new InvoiceDraftLine(
            line.Id, line.CatalogItemId, line.Description, line.Sku, line.Unit,
            line.QuantityMilliunits / 1_000m, new Money(line.UnitPriceHalalah), line.VatCategory,
            line.TaxExemptionReasonCode, line.TaxExemptionReason, line.OriginalInvoiceLineId)).ToArray(),
        DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUtcMs),
        DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUtcMs));

    private static CompanySnapshot ToCompanySnapshot(CompanyProfileEntity company) => new(
        company.Revision,
        PartySnapshot.Create(company.NameArabic, company.NameEnglish, company.VatNumber,
            company.CommercialRegistration, company.Address),
        company.Branch,
        company.OperatorName,
        company.LogoBytes?.ToArray(),
        company.LogoMimeType);

    private static string StateToken(
        DraftRecord draft,
        CompanyProfileEntity company,
        InvoiceEntity? original,
        IReadOnlyList<InvoiceEntity>? priorCredits) => JsonSerializer.Serialize(new
        {
            Draft = draft,
            Company = new
            {
                company.Revision,
                company.NameArabic,
                company.NameEnglish,
                company.VatNumber,
                company.CommercialRegistration,
                company.Branch,
                company.Address,
                company.OperatorName,
                company.DefaultPaymentMethod,
                company.LogoBytes,
                company.LogoMimeType,
            },
            Original = original is null ? null : InvoiceToken(original),
            PriorCredits = priorCredits?.Select(InvoiceToken).ToArray(),
        });

    private static object InvoiceToken(InvoiceEntity invoice) => new
    {
        invoice.Id,
        invoice.PublicNumber,
        invoice.DocumentType,
        invoice.OriginalInvoiceId,
        invoice.SellerNameArabic,
        invoice.SellerNameEnglish,
        invoice.SellerVatNumber,
        invoice.SellerCommercialRegistration,
        invoice.SellerBranch,
        invoice.SellerAddress,
        invoice.SellerLogoBytes,
        invoice.SellerLogoMimeType,
        invoice.CustomerNameArabic,
        invoice.CustomerNameEnglish,
        invoice.CustomerVatNumber,
        invoice.CustomerCommercialRegistration,
        invoice.CustomerAddress,
        invoice.GrandTotalHalalah,
        Lines = invoice.Lines.OrderBy(line => line.Position).Select(line => new
        {
            line.Id,
            line.Position,
            line.OriginalInvoiceLineId,
            line.Description,
            line.Sku,
            line.Unit,
            line.QuantityMilliunits,
            line.UnitPriceHalalah,
            line.VatCategory,
            line.TaxExemptionReasonCode,
            line.TaxExemptionReason,
            line.NetHalalah,
            line.VatHalalah,
            line.GrossHalalah,
        }).ToArray(),
    };

    private static void ValidateSale(DraftRecord draft, PartySnapshot seller)
    {
        InvoiceDraft validation = InvoiceDraft.Create(
            draft.BusinessDate, draft.DocumentType,
            new DraftParty(seller.NameArabic, seller.NameEnglish, seller.VatNumber,
                seller.CommercialRegistration, seller.Address),
            draft.Customer, draft.PaymentMethod, draft.OriginalInvoiceId, draft.CreatedAtUtc);
        validation.ReplaceLines(draft.Lines, draft.UpdatedAtUtc);
        InvoiceValidationResult result = InvoiceValidator.Validate(validation);
        if (!result.IsValid)
        {
            throw new DomainValidationException(string.Join(Environment.NewLine,
                result.Errors.Select(error => $"{error.Field}: {error.Message}")));
        }
    }

    private static PartySnapshot ToPartySnapshot(DraftParty party) => PartySnapshot.Create(
        party.Name, party.NameEnglish, party.VatNumber, party.CommercialRegistration, party.Address);

    private static InvoiceLineInput ToInput(InvoiceDraftLine line) => new(
        line.Description, line.Sku, line.Unit, line.Quantity, line.UnitPrice, line.VatCategory,
        line.Id, line.OriginalInvoiceLineId, line.TaxExemptionReasonCode, line.TaxExemptionReason);

    private static IssuedInvoice RehydrateSale(InvoiceEntity entity)
    {
        IReadOnlyList<InvoiceLineCalculation> lines = Array.AsReadOnly(entity.Lines.OrderBy(line => line.Position)
            .Select(line => new InvoiceLineCalculation(
                line.Id, line.OriginalInvoiceLineId, line.Description, line.Sku, line.Unit,
                line.QuantityMilliunits / 1_000m, new Money(line.UnitPriceHalalah), line.VatCategory,
                line.TaxExemptionReasonCode, line.TaxExemptionReason, new Money(line.NetHalalah),
                new Money(line.VatHalalah), new Money(line.GrossHalalah))).ToArray());
        return IssuedInvoice.CreateSale(
            new InvoiceNumber(entity.IssuanceYear, entity.Sequence), new DocumentSerial(entity.Id),
            IssueTiming.Capture(DateOnly.ParseExact(entity.BusinessDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTimeOffset.FromUnixTimeMilliseconds(entity.IssuedAtUtcMs)),
            PartySnapshot.Create(entity.SellerNameArabic, entity.SellerNameEnglish, entity.SellerVatNumber,
                entity.SellerCommercialRegistration, entity.SellerAddress),
            PartySnapshot.Create(entity.CustomerNameArabic, entity.CustomerNameEnglish, entity.CustomerVatNumber,
                entity.CustomerCommercialRegistration, entity.CustomerAddress),
            entity.SellerBranch, entity.OperatorName, entity.PaymentMethod.ToString(), entity.Title,
            entity.ShowNotes ? entity.Notes : null,
            new InvoiceCalculation(lines, new InvoiceTotals(new Money(entity.SubtotalHalalah),
                new Money(entity.VatHalalah), new Money(entity.GrandTotalHalalah))));
    }

    private static InvoiceEntity ToSaleEntity(
        IssuedInvoice invoice, DraftRecord draft, CompanySnapshot company, byte[] pdfBytes, byte[] pdfHash)
    {
        InvoiceEntity entity = BaseEntity(invoice, draft, company.LogoBytes, company.LogoMimeType);
        entity.SourceCustomerId = draft.CustomerId;
        AddLines(entity, invoice, draft.Lines.ToDictionary(line => line.Id));
        entity.Document = CreateDocument(invoice, pdfBytes, pdfHash);
        return entity;
    }

    private static InvoiceEntity ToCreditEntity(
        IssuedInvoice invoice, DraftRecord draft, InvoiceEntity original, CompanySnapshot company,
        byte[] pdfBytes, byte[] pdfHash)
    {
        InvoiceEntity entity = BaseEntity(invoice, draft, original.SellerLogoBytes, original.SellerLogoMimeType);
        entity.OriginalInvoiceId = original.Id;
        entity.SourceCustomerId = original.SourceCustomerId;
        Dictionary<Guid, InvoiceLineEntity> sources = original.Lines.ToDictionary(line => line.Id);
        for (int position = 0; position < invoice.Lines.Count; position++)
        {
            InvoiceLineCalculation line = invoice.Lines[position];
            InvoiceLineEntity source = sources[line.OriginalInvoiceLineId!.Value];
            entity.Lines.Add(ToLineEntity(invoice.Id, line, position, source.SourceCatalogItemId));
        }
        entity.Document = CreateDocument(invoice, pdfBytes, pdfHash);
        return entity;
    }

    private static InvoiceEntity BaseEntity(
        IssuedInvoice invoice, DraftRecord draft, byte[]? logoBytes, string? logoMimeType) => new()
        {
            Id = invoice.Id,
            IssuanceYear = invoice.Number.Year,
            Sequence = invoice.Number.Sequence,
            PublicNumber = invoice.Number.ToString(),
            DocumentType = invoice.Type,
            OriginalInvoiceId = invoice.OriginalInvoiceId,
            BusinessDate = invoice.Timing.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IssuedAtUtcMs = invoice.Timing.IssuedAtUtc.ToUnixTimeMilliseconds(),
            IssuedAtSaudiLocal = invoice.Timing.IssuedAtSaudi.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            IssuedSaudiOffsetMinutes = checked((int)invoice.Timing.IssuedAtSaudi.Offset.TotalMinutes),
            SellerNameArabic = invoice.Seller.NameArabic,
            SellerNameEnglish = invoice.Seller.NameEnglish,
            SellerVatNumber = invoice.Seller.VatNumber ?? string.Empty,
            SellerCommercialRegistration = invoice.Seller.CommercialRegistration,
            SellerBranch = invoice.Branch,
            SellerAddress = invoice.Seller.Address ?? string.Empty,
            SellerLogoBytes = logoBytes?.ToArray(),
            SellerLogoMimeType = logoMimeType,
            OperatorName = invoice.OperatorName,
            CustomerNameArabic = invoice.Customer.NameArabic,
            CustomerNameEnglish = invoice.Customer.NameEnglish,
            CustomerSearchName = ArabicSearchNormalizer.Normalize($"{invoice.Customer.NameArabic} {invoice.Customer.NameEnglish}"),
            CustomerVatNumber = invoice.Customer.VatNumber,
            CustomerCommercialRegistration = invoice.Customer.CommercialRegistration,
            CustomerAddress = invoice.Customer.Address,
            PaymentMethod = draft.PaymentMethod,
            Title = invoice.Title,
            Notes = invoice.Notes,
            ShowNotes = draft.ShowNotes,
            Currency = invoice.Currency,
            SubtotalHalalah = invoice.Totals.Subtotal.Halalah,
            VatHalalah = invoice.Totals.Vat.Halalah,
            GrandTotalHalalah = invoice.Totals.GrandTotal.Halalah,
        };

    private static void AddLines(
        InvoiceEntity entity, IssuedInvoice invoice, Dictionary<Guid, InvoiceDraftLine> sources)
    {
        for (int position = 0; position < invoice.Lines.Count; position++)
        {
            InvoiceLineCalculation line = invoice.Lines[position];
            sources.TryGetValue(line.Id, out InvoiceDraftLine? source);
            entity.Lines.Add(ToLineEntity(invoice.Id, line, position, source?.CatalogItemId));
        }
    }

    private static InvoiceLineEntity ToLineEntity(
        Guid invoiceId, InvoiceLineCalculation line, int position, Guid? sourceCatalogItemId) => new()
        {
            Id = line.Id,
            InvoiceId = invoiceId,
            Position = position,
            SourceCatalogItemId = sourceCatalogItemId,
            OriginalInvoiceLineId = line.OriginalInvoiceLineId,
            Description = line.Description,
            Sku = line.Sku,
            Unit = line.Unit,
            QuantityMilliunits = decimal.ToInt64(checked(line.Quantity * 1_000m)),
            UnitPriceHalalah = line.UnitPrice.Halalah,
            VatCategory = line.VatCategory,
            TaxExemptionReasonCode = line.TaxExemptionReasonCode,
            TaxExemptionReason = line.TaxExemptionReason,
            NetHalalah = line.Net.Halalah,
            VatHalalah = line.Vat.Halalah,
            GrossHalalah = line.Gross.Halalah,
        };

    private static InvoiceDocumentEntity CreateDocument(
        IssuedInvoice invoice, byte[] pdfBytes, byte[] pdfHash) => new()
        {
            InvoiceId = invoice.Id,
            PdfBytes = pdfBytes.ToArray(),
            Sha256 = pdfHash.ToArray(),
            ByteLength = pdfBytes.LongLength,
            MimeType = "application/pdf",
            CreatedAtUtcMs = invoice.Timing.IssuedAtUtc.ToUnixTimeMilliseconds(),
        };

    private static PersistenceConcurrencyException DraftConcurrencyFailure(Guid id, int revision) => new(
        $"Draft {id} does not exist at revision {revision}.", new DbUpdateConcurrencyException());

    private sealed record CompanySnapshot(
        int Revision, PartySnapshot Seller, string Branch, string OperatorName,
        byte[]? LogoBytes, string? LogoMimeType);

    private sealed record PreparedIssue(
        IssuedInvoice Invoice, DraftRecord Draft, CompanySnapshot Company, InvoiceEntity? Original,
        string StateToken, bool ShowNotes, byte[]? LogoBytes, string? LogoMimeType,
        string? OriginalPublicNumber);
}

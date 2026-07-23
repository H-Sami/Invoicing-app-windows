using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Domain.Tests.Invoices;

public sealed class InvoiceValidatorTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Validate_ReturnsStructuredErrorsForEveryInvalidField()
    {
        InvoiceDraft draft = InvoiceDraft.Create(
            new DateOnly(2026, 7, 23),
            InvoiceDocumentType.CreditNote,
            new DraftParty(" ", null, "123", null, null),
            new DraftParty(" ", null, "31012345678900A", "123", null),
            PaymentMethod.BankTransfer,
            null,
            CreatedAt);
        draft.ReplaceLines([
            new InvoiceDraftLine(
                Guid.CreateVersion7(),
                null,
                new string('x', 501),
                null,
                "unit",
                0m,
                new Money(-1),
                (VatCategory)999,
                null,
                null),
        ], CreatedAt.AddMinutes(1));

        InvoiceValidationResult result = InvoiceValidator.Validate(draft);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "seller.name");
        Assert.Contains(result.Errors, error => error.Field == "seller.vatNumber");
        Assert.Contains(result.Errors, error => error.Field == "customer.name");
        Assert.Contains(result.Errors, error => error.Field == "customer.vatNumber");
        Assert.Contains(result.Errors, error => error.Field == "customer.commercialRegistration");
        Assert.Contains(result.Errors, error => error.Field == "originalInvoiceId");
        Assert.Contains(result.Errors, error => error.Field == "lines[0].description");
        Assert.Contains(result.Errors, error => error.Field == "lines[0].quantity");
        Assert.Contains(result.Errors, error => error.Field == "lines[0].unitPrice");
        Assert.Contains(result.Errors, error => error.Field == "lines[0].vatCategory");
    }

    [Fact]
    public void Validate_RejectsNoLinesAndInvalidExemptionMetadata()
    {
        InvoiceDraft noLines = CreateValidDraft();
        InvoiceValidationResult noLinesResult = InvoiceValidator.Validate(noLines);
        Assert.Contains(noLinesResult.Errors, error => error.Field == "lines");

        InvoiceDraft missingReason = CreateValidDraft();
        missingReason.ReplaceLines([
            new InvoiceDraftLine(
                Guid.CreateVersion7(),
                null,
                "Export service",
                null,
                "unit",
                1m,
                Money.FromRiyals(10m),
                VatCategory.ZeroRated,
                null,
                null),
        ], CreatedAt.AddMinutes(1));

        InvoiceValidationResult reasonResult = InvoiceValidator.Validate(missingReason);
        Assert.Contains(reasonResult.Errors, error => error.Field == "lines[0].taxExemptionReasonCode");
        Assert.Contains(reasonResult.Errors, error => error.Field == "lines[0].taxExemptionReason");
    }

    [Fact]
    public void Validate_RejectsDuplicateNullAndMalformedLineIdentities()
    {
        InvoiceDraft draft = CreateValidDraft();
        Guid duplicateId = Guid.CreateVersion7();
        draft.ReplaceLines(
        [
            new InvoiceDraftLine(
                duplicateId,
                Guid.Empty,
                "A",
                null,
                "unit",
                1m,
                Money.FromRiyals(1m),
                VatCategory.Standard15,
                null,
                null),
            new InvoiceDraftLine(
                duplicateId,
                null,
                "B",
                null,
                "unit",
                1m,
                Money.FromRiyals(1m),
                VatCategory.Standard15,
                null,
                null),
            null!,
        ],
        CreatedAt.AddMinutes(1));

        InvoiceValidationResult result = InvoiceValidator.Validate(draft);

        Assert.Contains(result.Errors, error => error.Field == "lines[0].catalogItemId");
        Assert.Contains(result.Errors, error => error.Field == "lines[1].id" && error.Code == "duplicate");
        Assert.Contains(result.Errors, error => error.Field == "lines[2]" && error.Code == "required");
    }

    [Fact]
    public void ValidateCredit_ReportsRemainingQuantityAgainstOriginalLine()
    {
        Guid originalInvoiceId = Guid.CreateVersion7();
        Guid originalLineId = Guid.CreateVersion7();
        InvoiceDraft draft = InvoiceDraft.Create(
            new DateOnly(2026, 7, 23),
            InvoiceDocumentType.CreditNote,
            new DraftParty("MHC Technology", null, "310123456789003", "1010123456", "Riyadh"),
            new DraftParty("العميل", null, null, null, "الرياض"),
            PaymentMethod.BankTransfer,
            originalInvoiceId,
            CreatedAt);
        draft.ReplaceLines(
        [
            new InvoiceDraftLine(
                Guid.CreateVersion7(),
                null,
                "Return",
                null,
                "unit",
                3m,
                Money.FromRiyals(1m),
                VatCategory.Standard15,
                null,
                null,
                originalLineId),
        ],
        CreatedAt.AddMinutes(1));

        InvoiceValidationResult result = InvoiceValidator.Validate(
            draft,
            [new OriginalInvoiceLineCreditState(originalLineId, 5m, 3m)]);

        Assert.Contains(result.Errors, error =>
            error.Field == "lines[0].quantity" && error.Code == "exceeds_remaining");
    }

    [Fact]
    public void ReplaceLines_RejectsTimestampOlderThanCurrentDraftVersion()
    {
        InvoiceDraft draft = CreateValidDraft();
        InvoiceDraftLine line = new(
            Guid.CreateVersion7(),
            null,
            "Service",
            null,
            "unit",
            1m,
            Money.FromRiyals(1m),
            VatCategory.Standard15,
            null,
            null);
        draft.ReplaceLines([line], CreatedAt.AddMinutes(2));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            draft.ReplaceLines([line], CreatedAt.AddMinutes(1)));
        Assert.Equal(CreatedAt.AddMinutes(2), draft.UpdatedAtUtc);
    }

    [Fact]
    public void Validate_AcceptsCompleteSaleDraftWithoutAllocatingLegalIdentifiers()
    {
        InvoiceDraft draft = CreateValidDraft();
        draft.ReplaceLines([
            new InvoiceDraftLine(
                Guid.CreateVersion7(),
                null,
                "خدمة استشارية",
                "CONS-01",
                "ساعة",
                1.250m,
                Money.FromRiyals(200m),
                VatCategory.Standard15,
                null,
                null),
        ], CreatedAt.AddMinutes(1));

        InvoiceValidationResult result = InvoiceValidator.Validate(draft);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Null(draft.InvoiceNumber);
        Assert.Null(draft.DocumentSerial);
    }

    private static InvoiceDraft CreateValidDraft() => InvoiceDraft.Create(
        new DateOnly(2026, 7, 23),
        InvoiceDocumentType.TaxInvoice,
        new DraftParty("MHC Technology", null, "310123456789003", "1010123456", "Riyadh"),
        new DraftParty("العميل", null, null, null, "الرياض"),
        PaymentMethod.BankTransfer,
        null,
        CreatedAt);
}

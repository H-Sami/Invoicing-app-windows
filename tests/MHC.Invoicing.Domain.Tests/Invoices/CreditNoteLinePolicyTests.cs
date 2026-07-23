using MHC.Invoicing.Domain.Invoices;

namespace MHC.Invoicing.Domain.Tests.Invoices;

public sealed class CreditNoteLinePolicyTests
{
    [Fact]
    public void ValidateLines_ReturnsRemainingQuantitiesWithoutMutatingOriginal()
    {
        OriginalInvoiceLineCreditState original = new(Guid.NewGuid(), 5m, 1m);

        IReadOnlyList<ValidatedCreditLine> result = CreditNotePolicy.ValidateLines(
            [original],
            [new CreditLineRequest(original.OriginalLineId, 2m)]);

        ValidatedCreditLine line = Assert.Single(result);
        Assert.Equal(2m, line.CreditQuantity);
        Assert.Equal(2m, line.RemainingQuantityAfterCredit);
        Assert.Equal(1m, original.AlreadyCreditedQuantity);
    }

    [Fact]
    public void ValidateLines_RejectsQuantityAboveOriginalRemainingQuantity()
    {
        Guid lineId = Guid.NewGuid();

        Assert.Throws<DomainValidationException>(() => CreditNotePolicy.ValidateLines(
            [new OriginalInvoiceLineCreditState(lineId, 5m, 4m)],
            [new CreditLineRequest(lineId, 1.001m)]));
    }

    [Fact]
    public void ValidateLines_RejectsUnknownOrDuplicateOriginalLines()
    {
        Guid known = Guid.NewGuid();
        Guid unknown = Guid.NewGuid();
        OriginalInvoiceLineCreditState original = new(known, 5m, 0m);

        Assert.Throws<DomainValidationException>(() => CreditNotePolicy.ValidateLines(
            [original],
            [new CreditLineRequest(unknown, 1m)]));

        Assert.Throws<DomainValidationException>(() => CreditNotePolicy.ValidateLines(
            [original],
            [new CreditLineRequest(known, 1m), new CreditLineRequest(known, 1m)]));
    }
}

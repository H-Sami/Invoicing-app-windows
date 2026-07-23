using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Domain.Tests.Invoices;

public sealed class CreditNotePolicyTests
{
    [Fact]
    public void Validate_ReturnsRemainingCreditableAmount()
    {
        Money remaining = CreditNotePolicy.Validate(
            Guid.NewGuid(),
            Money.FromRiyals(1_000m),
            Money.FromRiyals(200m),
            Money.FromRiyals(300m));

        Assert.Equal(Money.FromRiyals(500m), remaining);
    }

    [Fact]
    public void Validate_RejectsCreditAboveRemainingAmount()
    {
        Assert.Throws<DomainValidationException>(() => CreditNotePolicy.Validate(
            Guid.NewGuid(),
            Money.FromRiyals(1_000m),
            Money.FromRiyals(800m),
            Money.FromRiyals(201m)));
    }

    [Fact]
    public void Validate_RejectsMissingOriginalInvoice()
    {
        Assert.Throws<DomainValidationException>(() => CreditNotePolicy.Validate(
            Guid.Empty,
            Money.FromRiyals(100m),
            Money.Zero,
            Money.FromRiyals(10m)));
    }
}

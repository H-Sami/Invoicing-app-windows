using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.Time;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Domain.Tests.Invoices;

public sealed class IssuedInvoiceTests
{
    [Fact]
    public void Create_SnapshotsPartiesLinesTotalsAndAuditTiming()
    {
        InvoiceCalculation calculation = InvoiceCalculator.Calculate(
        [
            new InvoiceLineInput("خدمة", "S-1", "وحدة", 2m, Money.FromRiyals(100m), VatCategory.Standard15),
        ]);
        PartySnapshot seller = PartySnapshot.Create("شركة MHC", "MHC", "310123456789003", "1010123456", "الرياض");
        PartySnapshot customer = PartySnapshot.Create("العميل", null, null, null, "جدة");
        IssueTiming timing = IssueTiming.Capture(new DateOnly(2026, 7, 20), new DateTimeOffset(2026, 7, 23, 10, 0, 0, TimeSpan.Zero));

        IssuedInvoice invoice = IssuedInvoice.CreateSale(
            new InvoiceNumber(2026, 100),
            DocumentSerial.Create(),
            timing,
            seller,
            customer,
            "الفرع الرئيسي",
            "سامي",
            "تحويل بنكي",
            "فاتورة ضريبية",
            "شكراً لتعاملكم",
            calculation);

        Assert.NotEqual(Guid.Empty, invoice.Id);
        Assert.Equal(invoice.Serial.Value, invoice.Id);
        Assert.Equal(invoice.Id.ToString("D"), invoice.Serial.ToString());
        Assert.Equal("MHC-2026-100", invoice.Number.ToString());
        Assert.Equal(calculation.Totals, invoice.Totals);
        Assert.Single(invoice.Lines);
        Assert.Equal("شركة MHC", invoice.Seller.NameArabic);
        Assert.Equal("العميل", invoice.Customer.NameArabic);
        Assert.Equal("سامي", invoice.OperatorName);
        Assert.Equal(new DateOnly(2026, 7, 20), invoice.Timing.BusinessDate);
    }

    [Fact]
    public void Create_RejectsInvoiceNumberYearDifferentFromSaudiIssuanceYear()
    {
        Assert.Throws<DomainValidationException>(() => CreateValid(
            new InvoiceNumber(2027, 100),
            new DateOnly(2026, 12, 31)));
    }

    [Fact]
    public void Create_AllowsBusinessDateInDifferentYearFromSaudiIssuance()
    {
        IssuedInvoice invoice = CreateValid(
            new InvoiceNumber(2026, 100),
            new DateOnly(2025, 12, 31));

        Assert.Equal(2026, invoice.Number.Year);
        Assert.Equal(2025, invoice.Timing.BusinessDate.Year);
    }

    [Fact]
    public void CreateCreditNote_RequiresOriginalInvoice()
    {
        Assert.Throws<ArgumentNullException>(() => IssuedInvoice.CreateCreditNote(
            null!,
            new InvoiceNumber(2026, 101),
            DocumentSerial.Create(),
            IssueTiming.Capture(
                new DateOnly(2026, 1, 2),
                new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero)),
            "سامي",
            "تحويل",
            null,
            null,
            Money.Zero,
            [],
            []));
    }

    [Fact]
    public void CreateSale_CannotCarryOriginalInvoiceReference()
    {
        IssuedInvoice invoice = CreateValid(
            new InvoiceNumber(2026, 100),
            new DateOnly(2026, 1, 1));

        Assert.Null(invoice.OriginalInvoiceId);
    }

    [Fact]
    public void CreateCreditNote_CopiesImmutableContextFromOriginalInvoice()
    {
        IssuedInvoice original = CreateValid(
            new InvoiceNumber(2026, 100),
            new DateOnly(2026, 1, 1));
        InvoiceLineCalculation originalLine = Assert.Single(original.Lines);

        IssuedInvoice credit = IssuedInvoice.CreateCreditNote(
            original,
            new InvoiceNumber(2026, 101),
            DocumentSerial.Create(),
            IssueTiming.Capture(
                new DateOnly(2026, 1, 2),
                new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero)),
            "المشغل الجديد",
            "تحويل",
            "إشعار دائن",
            null,
            Money.Zero,
            [new OriginalInvoiceLineCreditState(originalLine.Id, originalLine.Quantity, 0m)],
            [new CreditLineRequest(originalLine.Id, 1m)]);

        Assert.Equal(original.Id, credit.OriginalInvoiceId);
        Assert.Equal(original.Seller, credit.Seller);
        Assert.Equal(original.Customer, credit.Customer);
        Assert.Equal(original.Branch, credit.Branch);
        Assert.Equal(original.Currency, credit.Currency);
        Assert.Equal(InvoiceDocumentType.CreditNote, credit.Type);
        Assert.Equal("المشغل الجديد", credit.OperatorName);
        InvoiceLineCalculation creditLine = Assert.Single(credit.Lines);
        Assert.Equal(originalLine.Id, creditLine.OriginalInvoiceLineId);
        Assert.Equal(originalLine.UnitPrice, creditLine.UnitPrice);
        Assert.Equal(originalLine.VatCategory, creditLine.VatCategory);
        Assert.True(credit.Totals.GrandTotal > Money.Zero);
        Assert.Equal(-credit.Totals.GrandTotal, credit.SignedGrandTotal);
    }

    [Fact]
    public void CreateCreditNote_RejectsQuantityBeyondPersistedRemainingAmount()
    {
        IssuedInvoice original = CreateValid(
            new InvoiceNumber(2026, 100),
            new DateOnly(2026, 1, 1));
        InvoiceLineCalculation originalLine = Assert.Single(original.Lines);

        Assert.Throws<DomainValidationException>(() => IssuedInvoice.CreateCreditNote(
            original,
            new InvoiceNumber(2026, 101),
            DocumentSerial.Create(),
            IssueTiming.Capture(
                new DateOnly(2026, 1, 2),
                new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero)),
            "المشغل",
            "نقدي",
            null,
            null,
            Money.Zero,
            [new OriginalInvoiceLineCreditState(originalLine.Id, originalLine.Quantity, 0.5m)],
            [new CreditLineRequest(originalLine.Id, 0.501m)]));
    }

    [Fact]
    public void CreateSale_RejectsCreditLineLinkage()
    {
        InvoiceCalculation calculation = InvoiceCalculator.Calculate(
        [
            new InvoiceLineInput(
                "Service",
                null,
                "unit",
                1m,
                Money.FromRiyals(10m),
                VatCategory.Standard15,
                OriginalInvoiceLineId: Guid.CreateVersion7()),
        ]);

        Assert.Throws<DomainValidationException>(() => IssuedInvoice.CreateSale(
            new InvoiceNumber(2026, 100),
            DocumentSerial.Create(),
            IssueTiming.Capture(
                new DateOnly(2026, 1, 1),
                new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero)),
            PartySnapshot.Create("البائع", null, "310123456789003", null, null),
            PartySnapshot.Create("العميل", null, null, null, null),
            "الفرع",
            "المشغل",
            "نقدي",
            null,
            null,
            calculation));
    }

    [Fact]
    public void CreateCreditNote_RejectsIssuanceBeforeOriginalInvoice()
    {
        DateTimeOffset originalInstant = new(2026, 1, 2, 10, 0, 0, TimeSpan.Zero);
        IssuedInvoice original = CreateValid(
            new InvoiceNumber(2026, 100),
            new DateOnly(2026, 1, 2),
            IssueTiming.Capture(new DateOnly(2026, 1, 2), originalInstant));
        InvoiceLineCalculation originalLine = Assert.Single(original.Lines);

        Assert.Throws<DomainValidationException>(() => IssuedInvoice.CreateCreditNote(
            original,
            new InvoiceNumber(2026, 101),
            DocumentSerial.Create(),
            IssueTiming.Capture(new DateOnly(2026, 1, 1), originalInstant.AddTicks(-1)),
            "المشغل",
            "نقدي",
            null,
            null,
            Money.Zero,
            [new OriginalInvoiceLineCreditState(originalLine.Id, originalLine.Quantity, 0m)],
            [new CreditLineRequest(originalLine.Id, originalLine.Quantity)]));
    }

    [Fact]
    public void Create_RejectsInconsistentDefaultIssueTiming()
    {
        Assert.Throws<DomainValidationException>(() => CreateValid(
            new InvoiceNumber(2026, 100),
            new DateOnly(2026, 1, 1),
            default(IssueTiming)));
    }

    private static IssuedInvoice CreateValid(
        InvoiceNumber number,
        DateOnly date,
        IssueTiming? timing = null)
    {
        InvoiceCalculation calculation = InvoiceCalculator.Calculate(
        [
            new InvoiceLineInput("خدمة", null, "وحدة", 1m, Money.FromRiyals(10m), VatCategory.Standard15),
        ]);

        return IssuedInvoice.CreateSale(
            number,
            DocumentSerial.Create(),
            timing ?? IssueTiming.Capture(
                date,
                new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero)),
            PartySnapshot.Create("البائع", null, null, null, null),
            PartySnapshot.Create("العميل", null, null, null, null),
            "الفرع",
            "المشغل",
            "نقدي",
            null,
            null,
            calculation);
    }
}

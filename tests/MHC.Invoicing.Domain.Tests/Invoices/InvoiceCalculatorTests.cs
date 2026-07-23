using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Domain.Tests.Invoices;

public sealed class InvoiceCalculatorTests
{
    [Fact]
    public void Calculate_RoundsVatPerLineAndReconcilesTotals()
    {
        InvoiceLineInput[] lines =
        [
            new("خدمة", null, "وحدة", 1m, Money.FromRiyals(0.90m), VatCategory.Standard15),
            new("منتج", "SKU-1", "قطعة", 3m, Money.FromRiyals(0.33m), VatCategory.Standard15),
            new(
                "معفى",
                null,
                "وحدة",
                2m,
                Money.FromRiyals(5m),
                VatCategory.Exempt,
                TaxExemptionReasonCode: "VATEX-SA-29",
                TaxExemptionReason: "Exempt service"),
        ];

        InvoiceCalculation calculation = InvoiceCalculator.Calculate(lines);

        Assert.Equal(new Money(1_189), calculation.Totals.Subtotal);
        Assert.Equal(new Money(29), calculation.Totals.Vat);
        Assert.Equal(new Money(1_218), calculation.Totals.GrandTotal);
        Assert.Equal(calculation.Totals.GrandTotal, calculation.Lines.Aggregate(Money.Zero, (sum, line) => sum + line.Gross));
        Assert.Equal(new Money(14), calculation.Lines[0].Vat);
        Assert.Equal(new Money(15), calculation.Lines[1].Vat);
        Assert.Equal(Money.Zero, calculation.Lines[2].Vat);
        Assert.IsType<System.Collections.ObjectModel.ReadOnlyCollection<InvoiceLineCalculation>>(calculation.Lines);
    }

    [Fact]
    public void Calculate_ExposesAnImmutableLineCollection()
    {
        InvoiceCalculation calculation = InvoiceCalculator.Calculate(
        [
            new InvoiceLineInput("Service", null, "unit", 1m, Money.FromRiyals(10m), VatCategory.Standard15),
        ]);

        IList<InvoiceLineCalculation> lines = Assert.IsAssignableFrom<IList<InvoiceLineCalculation>>(calculation.Lines);
        Assert.Throws<NotSupportedException>(() => lines.Clear());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Calculate_RejectsNonPositiveQuantity(decimal quantity)
    {
        InvoiceLineInput line = new("خدمة", null, "وحدة", quantity, Money.FromRiyals(1m), VatCategory.Standard15);

        Assert.Throws<DomainValidationException>(() => InvoiceCalculator.Calculate([line]));
    }

    [Fact]
    public void Calculate_RejectsMoreThanThreeQuantityDecimals()
    {
        InvoiceLineInput line = new("خدمة", null, "وحدة", 1.2345m, Money.FromRiyals(1m), VatCategory.Standard15);

        Assert.Throws<DomainValidationException>(() => InvoiceCalculator.Calculate([line]));
    }

    [Fact]
    public void Calculate_UsesTheSharedQuantityBoundary()
    {
        InvoiceLineInput maximum = new(
            "خدمة",
            null,
            "وحدة",
            InvoiceRules.MaxQuantity,
            Money.Zero,
            VatCategory.Standard15);
        InvoiceLineInput excessive = maximum with { Quantity = InvoiceRules.MaxQuantity + 0.001m };

        Assert.Single(InvoiceCalculator.Calculate([maximum]).Lines);
        Assert.Throws<DomainValidationException>(() => InvoiceCalculator.Calculate([excessive]));
    }

    [Fact]
    public void Calculate_PreservesIdentityLinkAndTaxExemptionMetadata()
    {
        Guid lineId = Guid.CreateVersion7();
        Guid originalLineId = Guid.CreateVersion7();
        InvoiceLineInput input = new(
            "Export",
            null,
            "unit",
            1m,
            Money.FromRiyals(10m),
            VatCategory.ZeroRated,
            lineId,
            originalLineId,
            "VATEX-SA-32",
            "Export of goods");

        InvoiceLineCalculation line = Assert.Single(InvoiceCalculator.Calculate([input]).Lines);

        Assert.Equal(lineId, line.Id);
        Assert.Equal(originalLineId, line.OriginalInvoiceLineId);
        Assert.Equal("VATEX-SA-32", line.TaxExemptionReasonCode);
        Assert.Equal("Export of goods", line.TaxExemptionReason);
    }

    [Fact]
    public void Calculate_NormalizesWhitespaceExemptionMetadataForStandardRatedLine()
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
                TaxExemptionReasonCode: " ",
                TaxExemptionReason: "\t"),
        ]);

        InvoiceLineCalculation line = Assert.Single(calculation.Lines);
        Assert.Null(line.TaxExemptionReasonCode);
        Assert.Null(line.TaxExemptionReason);
    }

    [Fact]
    public void Calculate_RejectsEmptyInvoices()
    {
        Assert.Throws<DomainValidationException>(() => InvoiceCalculator.Calculate([]));
    }

    [Fact]
    public void Calculate_RejectsMissingDescription()
    {
        InvoiceLineInput line = new(" ", null, "وحدة", 1m, Money.FromRiyals(1m), VatCategory.Standard15);

        Assert.Throws<DomainValidationException>(() => InvoiceCalculator.Calculate([line]));
    }

    [Fact]
    public void Calculate_OneHundredDeterministicInvoicesAlwaysReconcile()
    {
        Random random = new(20_260_723);

        for (int invoiceIndex = 0; invoiceIndex < 100; invoiceIndex++)
        {
            int lineCount = random.Next(1, 21);
            List<InvoiceLineInput> inputs = new(lineCount);
            for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
            {
                decimal quantity = random.Next(1, 1_000_001) / 1_000m;
                Money price = new(random.NextInt64(0, 1_000_001));
                VatCategory category = (VatCategory)random.Next(1, 4);
                inputs.Add(new InvoiceLineInput(
                    $"Line {lineIndex}",
                    null,
                    "unit",
                    quantity,
                    price,
                    category,
                    TaxExemptionReasonCode: category == VatCategory.Standard15 ? null : "VATEX-SA-29",
                    TaxExemptionReason: category == VatCategory.Standard15 ? null : "Non-standard VAT"));
            }

            InvoiceCalculation calculation = InvoiceCalculator.Calculate(inputs);

            Assert.Equal(calculation.Totals.Subtotal, calculation.Lines.Aggregate(Money.Zero, (sum, line) => sum + line.Net));
            Assert.Equal(calculation.Totals.Vat, calculation.Lines.Aggregate(Money.Zero, (sum, line) => sum + line.Vat));
            Assert.Equal(calculation.Totals.GrandTotal, calculation.Lines.Aggregate(Money.Zero, (sum, line) => sum + line.Gross));
            Assert.All(calculation.Lines, line => Assert.Equal(line.Net + line.Vat, line.Gross));
        }
    }

    [Fact]
    public void Calculate_RejectsOverflowInsteadOfWrappingTotals()
    {
        InvoiceLineInput first = new(
            "A",
            null,
            "unit",
            1m,
            new Money(long.MaxValue),
            VatCategory.Exempt,
            TaxExemptionReasonCode: "VATEX-SA-29",
            TaxExemptionReason: "Exempt");
        InvoiceLineInput second = new(
            "B",
            null,
            "unit",
            1m,
            Money.FromRiyals(0.01m),
            VatCategory.Exempt,
            TaxExemptionReasonCode: "VATEX-SA-29",
            TaxExemptionReason: "Exempt");

        Assert.Throws<OverflowException>(() => InvoiceCalculator.Calculate([first, second]));
    }
}

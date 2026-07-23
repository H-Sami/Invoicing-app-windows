using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Domain.Tests.MoneyValues;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("0.135", 14)]
    [InlineData("-0.135", -14)]
    [InlineData("12.344", 1234)]
    [InlineData("12.345", 1235)]
    public void FromRiyals_RoundsToNearestHalalahAwayFromZero(string riyals, long expectedHalalah)
    {
        Money value = Money.FromRiyals(decimal.Parse(riyals, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(expectedHalalah, value.Halalah);
    }

    [Fact]
    public void Addition_UsesCheckedHalalahArithmetic()
    {
        Money result = new Money(90) + new Money(14);

        Assert.Equal(new Money(104), result);
    }

    [Fact]
    public void Addition_ThrowsWhenHalalahOverflows()
    {
        Assert.Throws<OverflowException>(() => new Money(long.MaxValue) + new Money(1));
    }

    [Fact]
    public void Multiply_RoundsOnlyAtTheHalalahBoundary()
    {
        Money result = Money.FromRiyals(0.90m).Multiply(1.15m);

        Assert.Equal(new Money(104), result);
    }

    [Fact]
    public void Riyals_ReturnsExactDecimalValue()
    {
        Assert.Equal(123.45m, new Money(12_345).Riyals);
    }

    [Fact]
    public void ToString_FormatsTwoDecimalRiyalsWithCurrency()
    {
        string formatted = Money.FromRiyals(1_234.5m).ToString(
            "N2",
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("1,234.50 SAR", formatted);
    }
}

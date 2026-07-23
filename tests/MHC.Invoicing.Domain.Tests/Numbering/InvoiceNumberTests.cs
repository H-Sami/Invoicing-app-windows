using MHC.Invoicing.Domain.Time;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Domain.Tests.Numbering;

public sealed class InvoiceNumberTests
{
    [Fact]
    public void ToString_UsesMhcYearAndSequence()
    {
        Assert.Equal("MHC-2026-100", new InvoiceNumber(2026, 100).ToString());
    }

    [Theory]
    [InlineData(1999, 100)]
    [InlineData(2026, 99)]
    public void Constructor_RejectsInvalidValues(int year, int sequence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InvoiceNumber(year, sequence));
    }

    [Fact]
    public void Year_ComesFromActualSaudiIssuanceTime_NotBusinessDate()
    {
        IssueTiming timing = IssueTiming.Capture(
            new DateOnly(2025, 12, 31),
            new DateTimeOffset(2026, 12, 31, 22, 0, 0, TimeSpan.Zero));

        InvoiceNumber value = InvoiceNumber.ForIssuance(timing, 100);

        Assert.Equal(2027, value.Year);
        Assert.Equal("MHC-2027-100", value.ToString());
    }
}

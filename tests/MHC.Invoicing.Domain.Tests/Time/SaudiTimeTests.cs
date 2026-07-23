using MHC.Invoicing.Domain.Time;

namespace MHC.Invoicing.Domain.Tests.Time;

public sealed class SaudiTimeTests
{
    [Fact]
    public void ToSaudiTime_UsesUtcPlusThreeWithoutDaylightSaving()
    {
        DateTimeOffset utc = new(2026, 7, 23, 10, 15, 0, TimeSpan.Zero);

        DateTimeOffset local = SaudiTime.ToLocal(utc);

        Assert.Equal(new DateTimeOffset(2026, 7, 23, 13, 15, 0, TimeSpan.FromHours(3)), local);
    }

    [Fact]
    public void Capture_StoresActualUtcAndEditableBusinessDateSeparately()
    {
        DateTimeOffset utc = new(2026, 12, 31, 22, 30, 0, TimeSpan.Zero);
        DateOnly selectedBusinessDate = new(2026, 12, 15);

        IssueTiming timing = IssueTiming.Capture(selectedBusinessDate, utc);

        Assert.Equal(selectedBusinessDate, timing.BusinessDate);
        Assert.Equal(utc, timing.IssuedAtUtc);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 1, 30, 0, TimeSpan.FromHours(3)), timing.IssuedAtSaudi);
    }
}

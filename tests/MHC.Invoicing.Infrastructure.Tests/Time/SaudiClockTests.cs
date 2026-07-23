using MHC.Invoicing.Infrastructure.Time;

namespace MHC.Invoicing.Infrastructure.Tests.Time;

public sealed class SaudiClockTests
{
    [Fact]
    public void SaudiNow_UsesArabStandardTimeFromUtcProvider()
    {
        DateTimeOffset utc = new(2026, 12, 31, 22, 30, 0, TimeSpan.Zero);
        SaudiClock clock = new(new FrozenTimeProvider(utc));

        Assert.Equal(utc, clock.UtcNow);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 1, 30, 0, TimeSpan.FromHours(3)), clock.SaudiNow);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

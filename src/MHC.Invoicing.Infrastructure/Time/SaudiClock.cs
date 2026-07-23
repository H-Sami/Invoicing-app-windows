using MHC.Invoicing.Application.Abstractions;
using MHC.Invoicing.Domain.Time;

namespace MHC.Invoicing.Infrastructure.Time;

public sealed class SaudiClock(TimeProvider timeProvider) : IClock
{

    public SaudiClock()
        : this(TimeProvider.System)
    {
    }

    public DateTimeOffset UtcNow => timeProvider.GetUtcNow().ToUniversalTime();

    public DateTimeOffset SaudiNow => SaudiTime.ToLocal(UtcNow);
}

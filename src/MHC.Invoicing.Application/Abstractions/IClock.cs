namespace MHC.Invoicing.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    DateTimeOffset SaudiNow { get; }
}

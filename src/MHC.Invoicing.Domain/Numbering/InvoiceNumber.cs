using MHC.Invoicing.Domain.Time;

namespace MHC.Invoicing.Domain.ValueObjects;

public readonly record struct InvoiceNumber
{
    public InvoiceNumber(int year, int sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, 2000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, 9999);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 100);

        Year = year;
        Sequence = sequence;
    }

    public int Year { get; }

    public int Sequence { get; }

    public static InvoiceNumber ForIssuance(IssueTiming timing, int sequence) =>
        new(timing.IssuedAtSaudi.Year, sequence);

    public override string ToString() => FormattableString.Invariant($"MHC-{Year:D4}-{Sequence}");
}

namespace MHC.Invoicing.Domain.ValueObjects;

public readonly record struct DocumentSerial
{
    public DocumentSerial(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Document serial cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static DocumentSerial Create() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

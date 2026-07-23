using MHC.Invoicing.Domain.Validation;

namespace MHC.Invoicing.Domain.Catalog;

public readonly record struct UnitOfMeasure
{
    private UnitOfMeasure(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static UnitOfMeasure Create(string value)
    {
        return new UnitOfMeasure(DomainTextRules.Required(
            value,
            DomainFieldLimits.Unit,
            nameof(value)));
    }

    public override string ToString() => Value;
}

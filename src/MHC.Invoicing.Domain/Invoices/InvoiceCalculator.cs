using MHC.Invoicing.Domain.Validation;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Domain.Invoices;

public enum VatCategory
{
    Standard15 = 1,
    ZeroRated = 2,
    Exempt = 3,
}

public static class InvoiceRules
{
    public const decimal MaxQuantity = 1_000_000m;
    public const int QuantityDecimalPlaces = 3;
}

public sealed class DomainValidationException(string message) : Exception(message);

public sealed record InvoiceLineInput(
    string Description,
    string? Sku,
    string Unit,
    decimal Quantity,
    Money UnitPrice,
    VatCategory VatCategory,
    Guid Id = default,
    Guid? OriginalInvoiceLineId = null,
    string? TaxExemptionReasonCode = null,
    string? TaxExemptionReason = null);

public sealed record InvoiceLineCalculation
{
    internal InvoiceLineCalculation(
        Guid id,
        Guid? originalInvoiceLineId,
        string description,
        string? sku,
        string unit,
        decimal quantity,
        Money unitPrice,
        VatCategory vatCategory,
        string? taxExemptionReasonCode,
        string? taxExemptionReason,
        Money net,
        Money vat,
        Money gross)
    {
        Id = id;
        OriginalInvoiceLineId = originalInvoiceLineId;
        Description = description;
        Sku = sku;
        Unit = unit;
        Quantity = quantity;
        UnitPrice = unitPrice;
        VatCategory = vatCategory;
        TaxExemptionReasonCode = taxExemptionReasonCode;
        TaxExemptionReason = taxExemptionReason;
        Net = net;
        Vat = vat;
        Gross = gross;
    }

    public Guid Id { get; }

    public Guid? OriginalInvoiceLineId { get; }

    public string Description { get; }

    public string? Sku { get; }

    public string Unit { get; }

    public decimal Quantity { get; }

    public Money UnitPrice { get; }

    public VatCategory VatCategory { get; }

    public string? TaxExemptionReasonCode { get; }

    public string? TaxExemptionReason { get; }

    public Money Net { get; }

    public Money Vat { get; }

    public Money Gross { get; }
}

public sealed record InvoiceTotals
{
    internal InvoiceTotals(Money subtotal, Money vat, Money grandTotal)
    {
        Subtotal = subtotal;
        Vat = vat;
        GrandTotal = grandTotal;
    }

    public Money Subtotal { get; }

    public Money Vat { get; }

    public Money GrandTotal { get; }
}

public sealed class InvoiceCalculation
{
    internal InvoiceCalculation(
        IReadOnlyList<InvoiceLineCalculation> lines,
        InvoiceTotals totals)
    {
        Lines = lines;
        Totals = totals;
    }

    public IReadOnlyList<InvoiceLineCalculation> Lines { get; }

    public InvoiceTotals Totals { get; }
}

public static class InvoiceCalculator
{
    private const decimal StandardVatRate = 0.15m;

    public static InvoiceCalculation Calculate(IReadOnlyCollection<InvoiceLineInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            throw new DomainValidationException("An invoice must contain at least one line.");
        }

        List<InvoiceLineCalculation> lines = new(inputs.Count);
        HashSet<Guid> lineIds = new();
        Money subtotal = Money.Zero;
        Money vatTotal = Money.Zero;
        Money grandTotal = Money.Zero;

        foreach (InvoiceLineInput input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            Validate(input);
            Guid lineId = input.Id == Guid.Empty ? Guid.CreateVersion7() : input.Id;
            if (!lineIds.Add(lineId))
            {
                throw new DomainValidationException("Invoice line identities must be unique.");
            }

            Money net = Money.FromRiyals(input.UnitPrice.Riyals * input.Quantity);
            Money vat = input.VatCategory == VatCategory.Standard15
                ? net.Multiply(StandardVatRate)
                : Money.Zero;
            Money gross = net + vat;

            lines.Add(new InvoiceLineCalculation(
                lineId,
                input.OriginalInvoiceLineId,
                input.Description.Trim(),
                NormalizeOptional(input.Sku),
                input.Unit.Trim(),
                input.Quantity,
                input.UnitPrice,
                input.VatCategory,
                NormalizeOptional(input.TaxExemptionReasonCode),
                NormalizeOptional(input.TaxExemptionReason),
                net,
                vat,
                gross));

            subtotal += net;
            vatTotal += vat;
            grandTotal += gross;
        }

        InvoiceCalculation calculation = new(
            Array.AsReadOnly(lines.ToArray()),
            new InvoiceTotals(subtotal, vatTotal, grandTotal));
        EnsureConsistent(calculation);
        return calculation;
    }

    internal static void EnsureConsistent(InvoiceCalculation calculation)
    {
        ArgumentNullException.ThrowIfNull(calculation);
        if (calculation.Lines.Count == 0)
        {
            throw new DomainValidationException("An issued invoice must contain at least one line.");
        }

        HashSet<Guid> identities = new();
        Money subtotal = Money.Zero;
        Money vat = Money.Zero;
        Money grandTotal = Money.Zero;
        foreach (InvoiceLineCalculation line in calculation.Lines)
        {
            if (line.Id == Guid.Empty || !identities.Add(line.Id))
            {
                throw new DomainValidationException("Issued line identities must be non-empty and unique.");
            }

            Validate(new InvoiceLineInput(
                line.Description,
                line.Sku,
                line.Unit,
                line.Quantity,
                line.UnitPrice,
                line.VatCategory,
                line.Id,
                line.OriginalInvoiceLineId,
                line.TaxExemptionReasonCode,
                line.TaxExemptionReason));

            Money expectedNet = Money.FromRiyals(line.UnitPrice.Riyals * line.Quantity);
            Money expectedVat = line.VatCategory == VatCategory.Standard15
                ? expectedNet.Multiply(StandardVatRate)
                : Money.Zero;
            if (line.Net != expectedNet || line.Vat != expectedVat || line.Gross != expectedNet + expectedVat)
            {
                throw new DomainValidationException("Issued line accounting values are inconsistent.");
            }

            subtotal += line.Net;
            vat += line.Vat;
            grandTotal += line.Gross;
        }

        if (calculation.Totals.Subtotal != subtotal ||
            calculation.Totals.Vat != vat ||
            calculation.Totals.GrandTotal != grandTotal ||
            grandTotal != subtotal + vat)
        {
            throw new DomainValidationException("Issued invoice totals do not reconcile with their lines.");
        }
    }

    private static void Validate(InvoiceLineInput input)
    {
        ValidateText(input.Description, DomainFieldLimits.LineDescription, "Line description");
        ValidateText(input.Unit, DomainFieldLimits.Unit, "Line unit");
        ValidateOptionalText(input.Sku, DomainFieldLimits.Sku, "Line SKU");

        if (input.Quantity <= 0 || input.Quantity > InvoiceRules.MaxQuantity)
        {
            throw new DomainValidationException(
                $"Quantity must be greater than zero and no more than {InvoiceRules.MaxQuantity}.");
        }

        if (decimal.Round(input.Quantity, InvoiceRules.QuantityDecimalPlaces, MidpointRounding.ToZero) != input.Quantity)
        {
            throw new DomainValidationException(
                $"Quantity can contain no more than {InvoiceRules.QuantityDecimalPlaces} decimal places.");
        }

        if (input.UnitPrice < Money.Zero)
        {
            throw new DomainValidationException("Unit price cannot be negative.");
        }

        if (!Enum.IsDefined(input.VatCategory))
        {
            throw new DomainValidationException("VAT category is invalid.");
        }

        if (input.OriginalInvoiceLineId == Guid.Empty)
        {
            throw new DomainValidationException("Original invoice-line identity cannot be empty.");
        }

        if (input.VatCategory is VatCategory.ZeroRated or VatCategory.Exempt)
        {
            ValidateText(
                input.TaxExemptionReasonCode,
                DomainFieldLimits.TaxExemptionReasonCode,
                "Tax exemption reason code");
            ValidateText(
                input.TaxExemptionReason,
                DomainFieldLimits.LineDescription,
                "Tax exemption reason");
        }
        else if (!string.IsNullOrWhiteSpace(input.TaxExemptionReasonCode) ||
            !string.IsNullOrWhiteSpace(input.TaxExemptionReason))
        {
            throw new DomainValidationException("Standard-rated lines cannot carry tax exemption metadata.");
        }
    }

    private static void ValidateText(string? value, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
        {
            throw new DomainValidationException($"{field} is required and cannot exceed {maxLength} characters.");
        }
    }

    private static void ValidateOptionalText(string? value, int maxLength, string field)
    {
        if (value?.Trim().Length > maxLength)
        {
            throw new DomainValidationException($"{field} cannot exceed {maxLength} characters.");
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

using MHC.Invoicing.Domain.Time;
using MHC.Invoicing.Domain.Validation;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Domain.Invoices;

public enum InvoiceDocumentType
{
    TaxInvoice = 1,
    CreditNote = 2,
}

public sealed record PartySnapshot(
    string NameArabic,
    string? NameEnglish,
    string? VatNumber,
    string? CommercialRegistration,
    string? Address)
{
    public static PartySnapshot Create(
        string nameArabic,
        string? nameEnglish,
        string? vatNumber,
        string? commercialRegistration,
        string? address)
    {
        return new PartySnapshot(
            DomainTextRules.Required(nameArabic, DomainFieldLimits.PartyName, nameof(nameArabic)),
            DomainTextRules.Optional(nameEnglish, DomainFieldLimits.PartyName, nameof(nameEnglish)),
            DomainTextRules.OptionalDigits(vatNumber, DomainFieldLimits.TaxIdentifier, nameof(vatNumber)),
            DomainTextRules.OptionalDigits(
                commercialRegistration,
                DomainFieldLimits.CommercialRegistration,
                nameof(commercialRegistration)),
            DomainTextRules.Optional(address, DomainFieldLimits.Address, nameof(address)));
    }
}

public sealed class IssuedInvoice
{
    private readonly IReadOnlyList<InvoiceLineCalculation> _lines;

    private IssuedInvoice(
        InvoiceNumber number,
        DocumentSerial serial,
        InvoiceDocumentType type,
        Guid? originalInvoiceId,
        IssueTiming timing,
        PartySnapshot seller,
        PartySnapshot customer,
        string branch,
        string operatorName,
        string paymentMethod,
        string? title,
        string? notes,
        InvoiceCalculation calculation)
    {
        Id = serial.Value;
        Number = number;
        Serial = serial;
        Type = type;
        OriginalInvoiceId = originalInvoiceId;
        Timing = timing;
        Seller = seller;
        Customer = customer;
        Branch = branch;
        OperatorName = operatorName;
        PaymentMethod = paymentMethod;
        Title = title;
        Notes = notes;
        _lines = Array.AsReadOnly(calculation.Lines.ToArray());
        Totals = calculation.Totals;
    }

    public Guid Id { get; }

    public InvoiceNumber Number { get; }

    public DocumentSerial Serial { get; }

    public InvoiceDocumentType Type { get; }

    public Guid? OriginalInvoiceId { get; }

    public IssueTiming Timing { get; }

    public PartySnapshot Seller { get; }

    public PartySnapshot Customer { get; }

    public string Branch { get; }

    public string OperatorName { get; }

    public string PaymentMethod { get; }

    public string Currency { get; } = Money.Currency;

    public string? Title { get; }

    public string? Notes { get; }

    public IReadOnlyList<InvoiceLineCalculation> Lines => _lines;

    public InvoiceTotals Totals { get; }

    public int AccountingSign => Type == InvoiceDocumentType.CreditNote ? -1 : 1;

    public Money SignedGrandTotal => AccountingSign == -1 ? -Totals.GrandTotal : Totals.GrandTotal;

    public static IssuedInvoice CreateSale(
        InvoiceNumber number,
        DocumentSerial serial,
        IssueTiming timing,
        PartySnapshot seller,
        PartySnapshot customer,
        string branch,
        string operatorName,
        string paymentMethod,
        string? title,
        string? notes,
        InvoiceCalculation calculation) => CreateCore(
            number,
            serial,
            InvoiceDocumentType.TaxInvoice,
            null,
            timing,
            seller,
            customer,
            branch,
            operatorName,
            paymentMethod,
            title,
            notes,
            calculation);

    internal static IssuedInvoice CreateCreditNote(
        IssuedInvoice original,
        InvoiceNumber number,
        DocumentSerial serial,
        IssueTiming timing,
        string operatorName,
        string paymentMethod,
        string? title,
        string? notes,
        Money alreadyCreditedGross,
        IReadOnlyCollection<OriginalInvoiceLineCreditState> originalCreditState,
        IReadOnlyCollection<CreditLineRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(originalCreditState);
        ArgumentNullException.ThrowIfNull(requests);
        if (original.Type != InvoiceDocumentType.TaxInvoice)
        {
            throw new DomainValidationException("A credit note must reference an issued sale invoice.");
        }

        if (timing.IssuedAtUtc < original.Timing.IssuedAtUtc)
        {
            throw new DomainValidationException("A credit note cannot be issued before its original invoice.");
        }

        IReadOnlyList<ValidatedCreditLine> validated = CreditNotePolicy.ValidateLines(originalCreditState, requests);
        Dictionary<Guid, InvoiceLineCalculation> originalLines = original.Lines.ToDictionary(line => line.Id);
        Dictionary<Guid, OriginalInvoiceLineCreditState> states = originalCreditState.ToDictionary(state => state.OriginalLineId);
        List<InvoiceLineInput> inputs = new(validated.Count);
        foreach (ValidatedCreditLine selected in validated)
        {
            if (!originalLines.TryGetValue(selected.OriginalLineId, out InvoiceLineCalculation? originalLine) ||
                !states.TryGetValue(selected.OriginalLineId, out OriginalInvoiceLineCreditState? state) ||
                state.SoldQuantity != originalLine.Quantity)
            {
                throw new DomainValidationException("Credit state does not match the original issued invoice.");
            }

            inputs.Add(new InvoiceLineInput(
                originalLine.Description,
                originalLine.Sku,
                originalLine.Unit,
                selected.CreditQuantity,
                originalLine.UnitPrice,
                originalLine.VatCategory,
                Guid.CreateVersion7(),
                originalLine.Id,
                originalLine.TaxExemptionReasonCode,
                originalLine.TaxExemptionReason));
        }

        InvoiceCalculation calculation = InvoiceCalculator.Calculate(inputs);
        CreditNotePolicy.Validate(
            original.Id,
            original.Totals.GrandTotal,
            alreadyCreditedGross,
            calculation.Totals.GrandTotal);

        return CreateCore(
            number,
            serial,
            InvoiceDocumentType.CreditNote,
            original.Id,
            timing,
            original.Seller,
            original.Customer,
            original.Branch,
            operatorName,
            paymentMethod,
            title,
            notes,
            calculation);
    }

    private static IssuedInvoice CreateCore(
        InvoiceNumber number,
        DocumentSerial serial,
        InvoiceDocumentType type,
        Guid? originalInvoiceId,
        IssueTiming timing,
        PartySnapshot seller,
        PartySnapshot customer,
        string branch,
        string operatorName,
        string paymentMethod,
        string? title,
        string? notes,
        InvoiceCalculation calculation)
    {
        ArgumentNullException.ThrowIfNull(seller);
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(calculation);
        InvoiceCalculator.EnsureConsistent(calculation);

        if (timing.IssuedAtUtc == default || timing.IssuedAtSaudi == default)
        {
            throw new DomainValidationException("Issuance timing must be captured from an actual instant.");
        }

        if (number.Year != timing.IssuedAtSaudi.Year)
        {
            throw new DomainValidationException("Invoice-number year must match the actual Saudi issuance year.");
        }

        if (type == InvoiceDocumentType.CreditNote &&
            (!originalInvoiceId.HasValue || originalInvoiceId.Value == Guid.Empty))
        {
            throw new DomainValidationException("A credit note must reference its original invoice.");
        }

        if (type == InvoiceDocumentType.TaxInvoice && originalInvoiceId is not null)
        {
            throw new DomainValidationException("A tax invoice cannot reference an original invoice as a credit note.");
        }

        if (type == InvoiceDocumentType.TaxInvoice &&
            calculation.Lines.Any(line => line.OriginalInvoiceLineId is not null))
        {
            throw new DomainValidationException("A tax-invoice line cannot reference an original invoice line.");
        }

        if (!Enum.IsDefined(type))
        {
            throw new DomainValidationException("Invoice document type is invalid.");
        }

        return new IssuedInvoice(
            number,
            serial,
            type,
            originalInvoiceId,
            timing,
            seller,
            customer,
            DomainTextRules.Required(branch, DomainFieldLimits.PartyName, nameof(branch)),
            DomainTextRules.Required(operatorName, DomainFieldLimits.PartyName, nameof(operatorName)),
            DomainTextRules.Required(paymentMethod, DomainFieldLimits.Phone, nameof(paymentMethod)),
            DomainTextRules.Optional(title, DomainFieldLimits.Title, nameof(title)),
            DomainTextRules.Optional(notes, DomainFieldLimits.Notes, nameof(notes)),
            calculation);
    }
}

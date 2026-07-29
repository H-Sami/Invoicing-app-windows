using MHC.Invoicing.Domain.Validation;

namespace MHC.Invoicing.Domain.Invoices;

public sealed record InvoiceValidationError(string Field, string Code, string Message);

public sealed class InvoiceValidationResult
{
    public InvoiceValidationResult(IEnumerable<InvoiceValidationError> errors)
    {
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    public IReadOnlyList<InvoiceValidationError> Errors { get; }

    public bool IsValid => Errors.Count == 0;
}

public static class InvoiceValidator
{
    public static InvoiceValidationResult Validate(
        InvoiceDraft draft,
        IReadOnlyCollection<OriginalInvoiceLineCreditState>? originalCreditState = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        List<InvoiceValidationError> errors = new();

        ValidateParty(draft.Seller, "seller", errors);
        ValidateParty(draft.Customer, "customer", errors);

        if (!Enum.IsDefined(draft.DocumentType))
        {
            Add(errors, "documentType", "invalid", "Document type is invalid.");
        }

        if (!Enum.IsDefined(draft.PaymentMethod))
        {
            Add(errors, "paymentMethod", "invalid", "Payment method is invalid.");
        }

        bool isCreditNote = draft.DocumentType == InvoiceDocumentType.CreditNote;
        if (isCreditNote && (!draft.OriginalInvoiceId.HasValue || draft.OriginalInvoiceId.Value == Guid.Empty))
        {
            Add(errors, "originalInvoiceId", "required", "A credit note must reference an issued sale invoice.");
        }
        else if (draft.DocumentType == InvoiceDocumentType.TaxInvoice && draft.OriginalInvoiceId is not null)
        {
            Add(errors, "originalInvoiceId", "not_allowed", "A sale invoice cannot reference an original invoice.");
        }

        Dictionary<Guid, OriginalInvoiceLineCreditState> creditState = BuildCreditState(
            isCreditNote,
            originalCreditState,
            errors);

        if (draft.Lines.Count == 0)
        {
            Add(errors, "lines", "required", "At least one invoice line is required.");
        }

        HashSet<Guid> lineIds = new();
        HashSet<Guid> originalLineIds = new();
        for (int index = 0; index < draft.Lines.Count; index++)
        {
            InvoiceDraftLine? line = draft.Lines[index];
            if (line is null)
            {
                Add(errors, $"lines[{index}]", "required", "Invoice line is required.");
                continue;
            }

            ValidateLine(line, index, lineIds, errors);
            if (isCreditNote)
            {
                ValidateCreditLine(line, index, creditState, originalLineIds, errors);
            }
            else if (line.OriginalInvoiceLineId is not null)
            {
                Add(
                    errors,
                    $"lines[{index}].originalInvoiceLineId",
                    "not_allowed",
                    "A sale line cannot reference an original invoice line.");
            }
        }

        return new InvoiceValidationResult(errors);
    }

    private static Dictionary<Guid, OriginalInvoiceLineCreditState> BuildCreditState(
        bool isCreditNote,
        IReadOnlyCollection<OriginalInvoiceLineCreditState>? originalCreditState,
        ICollection<InvoiceValidationError> errors)
    {
        Dictionary<Guid, OriginalInvoiceLineCreditState> states = new();
        if (!isCreditNote)
        {
            return states;
        }

        if (originalCreditState is null || originalCreditState.Count == 0)
        {
            Add(errors, "creditState", "required", "Original invoice-line credit state is required.");
            return states;
        }

        foreach (OriginalInvoiceLineCreditState state in originalCreditState)
        {
            if (state.OriginalLineId == Guid.Empty ||
                state.SoldQuantity <= 0 ||
                state.SoldQuantity > InvoiceRules.MaxQuantity ||
                state.AlreadyCreditedQuantity < 0 ||
                state.AlreadyCreditedQuantity > state.SoldQuantity ||
                !states.TryAdd(state.OriginalLineId, state))
            {
                Add(errors, "creditState", "invalid", "Original invoice-line credit state is invalid.");
            }
        }

        return states;
    }

    private static void ValidateParty(
        DraftParty party,
        string prefix,
        ICollection<InvoiceValidationError> errors)
    {
        if (party is null)
        {
            Add(errors, prefix, "required", "Party details are required.");
            return;
        }

        ValidateRequiredText(party.Name, DomainFieldLimits.PartyName, $"{prefix}.name", errors);
        ValidateOptionalText(party.NameEnglish, DomainFieldLimits.PartyName, $"{prefix}.nameEnglish", errors);
        ValidateOptionalText(party.Address, DomainFieldLimits.Address, $"{prefix}.address", errors);

        if (!IsDigits(party.VatNumber, DomainFieldLimits.TaxIdentifier))
        {
            Add(errors, $"{prefix}.vatNumber", "invalid_format", "VAT number must contain digits only.");
        }

        if (!IsDigits(party.CommercialRegistration, DomainFieldLimits.CommercialRegistration))
        {
            Add(
                errors,
                $"{prefix}.commercialRegistration",
                "invalid_format",
                "Commercial registration must contain digits only.");
        }
    }

    private static void ValidateLine(
        InvoiceDraftLine line,
        int index,
        HashSet<Guid> lineIds,
        ICollection<InvoiceValidationError> errors)
    {
        string prefix = $"lines[{index}]";
        if (line.Id == Guid.Empty)
        {
            Add(errors, $"{prefix}.id", "required", "Line identity is required.");
        }
        else if (!lineIds.Add(line.Id))
        {
            Add(errors, $"{prefix}.id", "duplicate", "Line identity must be unique within the invoice.");
        }

        if (line.CatalogItemId == Guid.Empty)
        {
            Add(errors, $"{prefix}.catalogItemId", "invalid", "Catalog item identity cannot be empty.");
        }

        ValidateRequiredText(line.Description, DomainFieldLimits.LineDescription, $"{prefix}.description", errors);
        ValidateOptionalText(line.Sku, DomainFieldLimits.Sku, $"{prefix}.sku", errors);
        ValidateRequiredText(line.Unit, DomainFieldLimits.Unit, $"{prefix}.unit", errors);

        if (line.Quantity <= 0 ||
            line.Quantity > InvoiceRules.MaxQuantity ||
            decimal.Round(line.Quantity, InvoiceRules.QuantityDecimalPlaces, MidpointRounding.ToZero) != line.Quantity)
        {
            Add(errors, $"{prefix}.quantity", "invalid", "Quantity must be positive, bounded, and use at most three decimal places.");
        }

        if (line.UnitPrice.Halalah < 0)
        {
            Add(errors, $"{prefix}.unitPrice", "invalid", "Sale unit price cannot be negative.");
        }

        if (!Enum.IsDefined(line.VatCategory))
        {
            Add(errors, $"{prefix}.vatCategory", "invalid", "VAT category is invalid.");
            return;
        }

        if (line.VatCategory is VatCategory.ZeroRated or VatCategory.Exempt)
        {
            ValidateRequiredText(
                line.TaxExemptionReasonCode,
                DomainFieldLimits.TaxExemptionReasonCode,
                $"{prefix}.taxExemptionReasonCode",
                errors);
            ValidateRequiredText(
                line.TaxExemptionReason,
                DomainFieldLimits.LineDescription,
                $"{prefix}.taxExemptionReason",
                errors);
        }
        else
        {
            ValidateAbsentText(line.TaxExemptionReasonCode, $"{prefix}.taxExemptionReasonCode", errors);
            ValidateAbsentText(line.TaxExemptionReason, $"{prefix}.taxExemptionReason", errors);
        }
    }

    private static void ValidateCreditLine(
        InvoiceDraftLine line,
        int index,
        Dictionary<Guid, OriginalInvoiceLineCreditState> creditState,
        HashSet<Guid> selectedOriginalLines,
        ICollection<InvoiceValidationError> errors)
    {
        string field = $"lines[{index}].originalInvoiceLineId";
        if (!line.OriginalInvoiceLineId.HasValue || line.OriginalInvoiceLineId.Value == Guid.Empty)
        {
            Add(errors, field, "required", "A credit line must reference its original invoice line.");
            return;
        }

        Guid originalLineId = line.OriginalInvoiceLineId.Value;
        if (!selectedOriginalLines.Add(originalLineId))
        {
            Add(errors, field, "duplicate", "An original invoice line cannot be selected more than once.");
            return;
        }

        if (!creditState.TryGetValue(originalLineId, out OriginalInvoiceLineCreditState? state))
        {
            Add(errors, field, "not_found", "The selected line does not belong to the original invoice.");
            return;
        }

        decimal remaining = state.SoldQuantity - state.AlreadyCreditedQuantity;
        if (line.Quantity > remaining)
        {
            Add(
                errors,
                $"lines[{index}].quantity",
                "exceeds_remaining",
                "Credit quantity exceeds the original line's remaining quantity.");
        }
    }

    private static bool IsDigits(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ||
        (value.Trim().Length <= maxLength && value.Trim().All(char.IsAsciiDigit));

    private static void ValidateRequiredText(
        string? value,
        int maxLength,
        string field,
        ICollection<InvoiceValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(errors, field, "required", "A value is required.");
        }
        else if (value.Trim().Length > maxLength)
        {
            Add(errors, field, "too_long", $"Value cannot exceed {maxLength} characters.");
        }
    }

    private static void ValidateOptionalText(
        string? value,
        int maxLength,
        string field,
        ICollection<InvoiceValidationError> errors)
    {
        if (value?.Trim().Length > maxLength)
        {
            Add(errors, field, "too_long", $"Value cannot exceed {maxLength} characters.");
        }
    }

    private static void ValidateAbsentText(
        string? value,
        string field,
        ICollection<InvoiceValidationError> errors)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Add(errors, field, "not_allowed", "Tax exemption metadata is allowed only for zero-rated or exempt lines.");
        }
    }

    private static void Add(
        ICollection<InvoiceValidationError> errors,
        string field,
        string code,
        string message) => errors.Add(new InvoiceValidationError(field, code, message));
}

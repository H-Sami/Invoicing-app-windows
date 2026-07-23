using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Domain.Invoices;

public sealed record OriginalInvoiceLineCreditState(
    Guid OriginalLineId,
    decimal SoldQuantity,
    decimal AlreadyCreditedQuantity);

public sealed record CreditLineRequest(Guid OriginalLineId, decimal Quantity);

public sealed record ValidatedCreditLine(
    Guid OriginalLineId,
    decimal CreditQuantity,
    decimal RemainingQuantityAfterCredit);

public static class CreditNotePolicy
{
    public static Money Validate(
        Guid originalInvoiceId,
        Money originalGross,
        Money alreadyCredited,
        Money requestedCredit)
    {
        if (originalInvoiceId == Guid.Empty)
        {
            throw new DomainValidationException("A credit note must reference its original invoice.");
        }

        if (originalGross <= Money.Zero || alreadyCredited < Money.Zero)
        {
            throw new DomainValidationException("Original and credited totals are invalid.");
        }

        if (requestedCredit <= Money.Zero)
        {
            throw new DomainValidationException("Credit amount must be greater than zero.");
        }

        Money available = originalGross - alreadyCredited;
        if (requestedCredit > available)
        {
            throw new DomainValidationException("Credit amount exceeds the remaining creditable total.");
        }

        return available - requestedCredit;
    }

    public static IReadOnlyList<ValidatedCreditLine> ValidateLines(
        IReadOnlyCollection<OriginalInvoiceLineCreditState> originalLines,
        IReadOnlyCollection<CreditLineRequest> requests)
    {
        if (originalLines.Count == 0 || requests.Count == 0)
        {
            throw new DomainValidationException("A credit note must select at least one original invoice line.");
        }

        Dictionary<Guid, OriginalInvoiceLineCreditState> states = new(originalLines.Count);
        foreach (OriginalInvoiceLineCreditState state in originalLines)
        {
            if (state.OriginalLineId == Guid.Empty ||
                state.SoldQuantity <= 0 ||
                state.SoldQuantity > InvoiceRules.MaxQuantity ||
                state.AlreadyCreditedQuantity < 0 ||
                state.AlreadyCreditedQuantity > state.SoldQuantity ||
                !states.TryAdd(state.OriginalLineId, state))
            {
                throw new DomainValidationException("Original invoice-line credit state is invalid.");
            }
        }

        HashSet<Guid> selected = new();
        List<ValidatedCreditLine> validated = new(requests.Count);
        foreach (CreditLineRequest request in requests)
        {
            if (!selected.Add(request.OriginalLineId))
            {
                throw new DomainValidationException("An original invoice line cannot be selected more than once.");
            }

            if (!states.TryGetValue(request.OriginalLineId, out OriginalInvoiceLineCreditState? state) ||
                state is null)
            {
                throw new DomainValidationException("The selected line does not belong to the original invoice.");
            }

            if (request.Quantity <= 0 ||
                request.Quantity > InvoiceRules.MaxQuantity ||
                decimal.Round(
                    request.Quantity,
                    InvoiceRules.QuantityDecimalPlaces,
                    MidpointRounding.ToZero) != request.Quantity)
            {
                throw new DomainValidationException("Credit quantity must be positive with no more than three decimal places.");
            }

            decimal remaining = state.SoldQuantity - state.AlreadyCreditedQuantity;
            if (request.Quantity > remaining)
            {
                throw new DomainValidationException("Credit quantity exceeds the original line's remaining quantity.");
            }

            validated.Add(new ValidatedCreditLine(
                request.OriginalLineId,
                request.Quantity,
                remaining - request.Quantity));
        }

        return Array.AsReadOnly(validated.ToArray());
    }
}

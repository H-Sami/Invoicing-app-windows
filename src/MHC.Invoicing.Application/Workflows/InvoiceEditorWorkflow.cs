using MHC.Invoicing.Application.Customers;
using MHC.Invoicing.Application.Drafts;
using MHC.Invoicing.Application.Items;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.Time;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Application.Workflows;

public sealed record IssuedInvoiceReference(
    Guid Id,
    string PublicNumber,
    InvoiceDocumentType DocumentType);

public interface IInvoiceEditorLookup
{
    Task<IReadOnlyList<CustomerSuggestion>> SearchCustomersAsync(
        string? searchText,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CatalogItemSuggestion>> SearchCatalogAsync(
        string? searchText,
        CancellationToken cancellationToken = default);

    Task<InvoiceDraftLine> SelectCatalogItemAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public interface IInvoiceEditorIssuance
{
    Task<IssuedInvoiceReference> IssueAsync(
        Guid draftId,
        int expectedRevision,
        InvoiceDocumentType documentType,
        CancellationToken cancellationToken = default);
}

public interface IInvoiceEditorDocuments
{
    Task PreviewAsync(IssuedInvoiceReference invoice, CancellationToken cancellationToken = default);

    Task PrintAsync(IssuedInvoiceReference invoice, CancellationToken cancellationToken = default);

    Task<bool> ExportAsync(IssuedInvoiceReference invoice, CancellationToken cancellationToken = default);
}

public sealed record InvoiceEditorCompanyProfile(bool IsReady);

public interface IInvoiceEditorCompanyProfile
{
    Task<InvoiceEditorCompanyProfile> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class CompanyProfileNotReadyException()
    : InvalidOperationException("Complete the company profile before issuing an invoice.");

public enum InvoiceEditorSaveStatus
{
    Saved,
    Saving,
    Conflict,
    Error,
}

public sealed record InvoiceEditorState(
    DraftRecord Draft,
    int Revision,
    InvoiceEditorSaveStatus SaveStatus,
    Money Subtotal,
    Money Vat,
    Money GrandTotal,
    IReadOnlyList<InvoiceValidationError> Errors,
    IssuedInvoiceReference? IssuedInvoice,
    bool IsCompanyProfileReady)
{
    public bool CanIssue => IsCompanyProfileReady &&
        Errors.Count == 0 &&
        Draft.Lines.Count > 0 &&
        SaveStatus == InvoiceEditorSaveStatus.Saved;
}

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The semaphore has workflow lifetime and never exposes its optional wait handle.")]
public sealed class InvoiceEditorWorkflow
{
    private readonly DraftAutosaveService _autosave;
    private readonly IInvoiceEditorDocuments _documents;
    private readonly IInvoiceEditorIssuance _issuance;
    private readonly IInvoiceEditorLookup _lookup;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IInvoiceEditorCompanyProfile _companyProfile;
    private readonly IDraftRepository _repository;
    private readonly TimeProvider _timeProvider;

    public InvoiceEditorWorkflow(
        IDraftRepository repository,
        DraftAutosaveService autosave,
        IInvoiceEditorLookup lookup,
        IInvoiceEditorIssuance issuance,
        IInvoiceEditorDocuments documents,
        IInvoiceEditorCompanyProfile companyProfile,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(autosave);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(issuance);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(companyProfile);
        _repository = repository;
        _autosave = autosave;
        _lookup = lookup;
        _issuance = issuance;
        _documents = documents;
        _companyProfile = companyProfile;
        _timeProvider = timeProvider ?? TimeProvider.System;
        State = new InvoiceEditorState(
            CreateDraft(_timeProvider.GetUtcNow()),
            0,
            InvoiceEditorSaveStatus.Saving,
            Money.Zero,
            Money.Zero,
            Money.Zero,
            [],
            null,
            false);
    }

    public InvoiceEditorState State { get; private set; }

    public async Task InitializeAsync(Guid? draftId = null, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InvoiceEditorCompanyProfile profile = await _companyProfile.GetAsync(cancellationToken).ConfigureAwait(false);
            if (draftId.HasValue)
            {
                VersionedDraft loaded = await _repository.GetAsync(draftId.Value, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"Draft {draftId.Value} was not found.");
                State = CreateState(
                    loaded.Draft,
                    loaded.Revision,
                    InvoiceEditorSaveStatus.Saved,
                    profile.IsReady);
                return;
            }

            DraftRecord draft = CreateDraft(_timeProvider.GetUtcNow());
            VersionedDraft saved = await _repository.SaveAsync(draft, null, cancellationToken).ConfigureAwait(false);
            State = CreateState(saved.Draft, saved.Revision, InvoiceEditorSaveStatus.Saved, profile.IsReady);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<IReadOnlyList<CustomerSuggestion>> SearchCustomersAsync(
        string? searchText,
        CancellationToken cancellationToken = default) =>
        _lookup.SearchCustomersAsync(searchText, cancellationToken);

    public Task<IReadOnlyList<CatalogItemSuggestion>> SearchCatalogAsync(
        string? searchText,
        CancellationToken cancellationToken = default) =>
        _lookup.SearchCatalogAsync(searchText, cancellationToken);

    public Task SelectCustomerAsync(
        CustomerSuggestion customer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);
        return SaveMutationAsync(
            draft => draft with
            {
                CustomerId = customer.Id,
                Customer = new DraftParty(
                    customer.NameArabic,
                    customer.NameEnglish,
                    customer.VatNumber,
                    customer.CommercialRegistration,
                    customer.Address),
            },
            cancellationToken);
    }

    public Task SetCustomerSnapshotAsync(
        DraftParty customer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);
        return SaveMutationAsync(
            draft => draft with { Customer = customer },
            cancellationToken);
    }

    public async Task AddCatalogItemAsync(Guid catalogItemId, CancellationToken cancellationToken = default)
    {
        InvoiceDraftLine line = await _lookup.SelectCatalogItemAsync(catalogItemId, cancellationToken)
            .ConfigureAwait(false);
        await SaveMutationAsync(
            draft => draft with { Lines = [.. draft.Lines, line] },
            cancellationToken).ConfigureAwait(false);
    }

    public Task AddOneOffLineAsync(
        string description,
        string? sku,
        string unit,
        decimal quantity,
        Money unitPrice,
        VatCategory vatCategory,
        string? taxExemptionReasonCode = null,
        string? taxExemptionReason = null,
        CancellationToken cancellationToken = default)
    {
        InvoiceDraftLine line = new(
            Guid.CreateVersion7(),
            null,
            description,
            sku,
            unit,
            quantity,
            unitPrice,
            vatCategory,
            taxExemptionReasonCode,
            taxExemptionReason);
        InvoiceCalculator.Calculate([ToInput(line)]);
        return SaveMutationAsync(
            draft => draft with { Lines = [.. draft.Lines, line] },
            cancellationToken);
    }

    public Task RemoveLineAsync(Guid lineId, CancellationToken cancellationToken = default) =>
        SaveMutationAsync(
            draft => draft with { Lines = draft.Lines.Where(line => line.Id != lineId).ToArray() },
            cancellationToken);

    public Task SetBusinessDateAsync(DateOnly businessDate, CancellationToken cancellationToken = default) =>
        SaveMutationAsync(draft => draft with { BusinessDate = businessDate }, cancellationToken);

    public Task SetPaymentMethodAsync(
        PaymentMethod paymentMethod,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(paymentMethod))
        {
            throw new ArgumentOutOfRangeException(nameof(paymentMethod));
        }

        return SaveMutationAsync(
            draft => draft with { PaymentMethod = paymentMethod },
            cancellationToken);
    }

    public async Task UpdateLineAsync(
        Guid lineId,
        decimal quantity,
        Money unitPrice,
        VatCategory vatCategory,
        string? taxExemptionReasonCode = null,
        string? taxExemptionReason = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int index = State.Draft.Lines.ToList().FindIndex(line => line.Id == lineId);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Invoice line {lineId} was not found.");
            }

            InvoiceDraftLine current = State.Draft.Lines[index];
            InvoiceDraftLine candidate = current with
            {
                Quantity = quantity,
                UnitPrice = unitPrice,
                VatCategory = vatCategory,
                TaxExemptionReasonCode = vatCategory == VatCategory.Standard15 ? null : taxExemptionReasonCode,
                TaxExemptionReason = vatCategory == VatCategory.Standard15 ? null : taxExemptionReason,
            };
            InvoiceValidationError? error = ValidateLine(candidate, index);
            if (error is not null)
            {
                State = State with { Errors = [error] };
                return;
            }

            await SaveMutationCoreAsync(
                draft => draft with
                {
                    Lines = draft.Lines.Select(line => line.Id == lineId ? candidate : line).ToArray(),
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<IssuedInvoiceReference?> IssueAsync(
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        if (!confirmed)
        {
            return null;
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!State.CanIssue)
            {
                if (!State.IsCompanyProfileReady)
                {
                    throw new CompanyProfileNotReadyException();
                }

                throw new InvalidOperationException("The persisted draft must be valid and saved before issuance.");
            }

            IssuedInvoiceReference issued = await _issuance.IssueAsync(
                State.Draft.Id,
                State.Revision,
                State.Draft.DocumentType,
                cancellationToken).ConfigureAwait(false);
            State = State with { IssuedInvoice = issued };
            return issued;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task PreviewAsync(CancellationToken cancellationToken = default) =>
        _documents.PreviewAsync(RequireIssuedInvoice(), cancellationToken);

    public Task PrintAsync(CancellationToken cancellationToken = default) =>
        _documents.PrintAsync(RequireIssuedInvoice(), cancellationToken);

    public Task<bool> ExportAsync(CancellationToken cancellationToken = default) =>
        _documents.ExportAsync(RequireIssuedInvoice(), cancellationToken);

    private IssuedInvoiceReference RequireIssuedInvoice() =>
        State.IssuedInvoice ?? throw new InvalidOperationException(
            "Preview, print, and export are available only after the invoice is issued.");

    private async Task SaveMutationAsync(
        Func<DraftRecord, DraftRecord> mutation,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveMutationCoreAsync(mutation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task SaveMutationCoreAsync(
        Func<DraftRecord, DraftRecord> mutation,
        CancellationToken cancellationToken)
    {
        int expectedRevision = State.Revision;
        bool isCompanyProfileReady = State.IsCompanyProfileReady;
        DraftRecord changed = mutation(State.Draft) with
        {
            UpdatedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
        };
        State = CreateState(
            changed,
            expectedRevision,
            InvoiceEditorSaveStatus.Saving,
            isCompanyProfileReady);
        try
        {
            DraftAutosaveResult result = await _autosave.SaveAfterDebounceAsync(
                changed,
                expectedRevision,
                cancellationToken).ConfigureAwait(false);
            if (result.Status == DraftAutosaveStatus.Conflict)
            {
                State = CreateState(
                    changed,
                    expectedRevision,
                    InvoiceEditorSaveStatus.Conflict,
                    isCompanyProfileReady);
                return;
            }

            VersionedDraft saved = result.SavedDraft!;
            State = CreateState(
                saved.Draft,
                saved.Revision,
                InvoiceEditorSaveStatus.Saved,
                isCompanyProfileReady);
        }
        catch
        {
            State = CreateState(
                changed,
                expectedRevision,
                InvoiceEditorSaveStatus.Error,
                isCompanyProfileReady);
            throw;
        }
    }

    private static DraftRecord CreateDraft(DateTimeOffset now)
    {
        DateTimeOffset utcNow = now.ToUniversalTime();
        return new DraftRecord(
            Guid.CreateVersion7(),
            InvoiceDocumentType.TaxInvoice,
            null,
            null,
            DateOnly.FromDateTime(SaudiTime.ToLocal(utcNow).DateTime),
            new DraftParty("عميل نقدي", null, null, null, null),
            (PaymentMethod)0,
            null,
            null,
            false,
            [],
            utcNow,
            utcNow);
    }

    private static InvoiceEditorState CreateState(
        DraftRecord draft,
        int revision,
        InvoiceEditorSaveStatus saveStatus,
        bool isCompanyProfileReady)
    {
        InvoiceValidationError[] paymentErrors = Enum.IsDefined(draft.PaymentMethod)
            ? []
            : [new InvoiceValidationError("paymentMethod", "invalid", "Select a payment method.")];
        if (draft.Lines.Count == 0)
        {
            return new InvoiceEditorState(
                draft,
                revision,
                saveStatus,
                Money.Zero,
                Money.Zero,
                Money.Zero,
                [
                    new InvoiceValidationError("lines", "required", "At least one invoice line is required."),
                    .. paymentErrors,
                ],
                null,
                isCompanyProfileReady);
        }

        try
        {
            InvoiceCalculation calculation = InvoiceCalculator.Calculate(draft.Lines.Select(ToInput).ToArray());
            return new InvoiceEditorState(
                draft,
                revision,
                saveStatus,
                calculation.Totals.Subtotal,
                calculation.Totals.Vat,
                calculation.Totals.GrandTotal,
                paymentErrors,
                null,
                isCompanyProfileReady);
        }
        catch (DomainValidationException exception)
        {
            return new InvoiceEditorState(
                draft,
                revision,
                saveStatus,
                Money.Zero,
                Money.Zero,
                Money.Zero,
                [new InvoiceValidationError("lines", "invalid", exception.Message), .. paymentErrors],
                null,
                isCompanyProfileReady);
        }
    }

    private static InvoiceValidationError? ValidateLine(InvoiceDraftLine line, int index)
    {
        string prefix = $"lines[{index}]";
        if (line.Quantity <= 0 ||
            line.Quantity > InvoiceRules.MaxQuantity ||
            decimal.Round(line.Quantity, InvoiceRules.QuantityDecimalPlaces, MidpointRounding.ToZero) != line.Quantity)
        {
            return new InvoiceValidationError(
                $"{prefix}.quantity",
                "invalid",
                "Quantity must be positive, no more than 1,000,000, and use at most three decimals.");
        }

        if (line.UnitPrice < Money.Zero)
        {
            return new InvoiceValidationError($"{prefix}.unitPrice", "invalid", "Unit price cannot be negative.");
        }

        if (!Enum.IsDefined(line.VatCategory))
        {
            return new InvoiceValidationError($"{prefix}.vatCategory", "invalid", "VAT category is invalid.");
        }

        if (line.VatCategory is VatCategory.ZeroRated or VatCategory.Exempt &&
            (string.IsNullOrWhiteSpace(line.TaxExemptionReasonCode) ||
                string.IsNullOrWhiteSpace(line.TaxExemptionReason)))
        {
            return new InvoiceValidationError(
                $"{prefix}.taxExemptionReason",
                "required",
                "An exemption code and reason are required for zero-rated or exempt lines.");
        }

        return null;
    }

    private static InvoiceLineInput ToInput(InvoiceDraftLine line) => new(
        line.Description,
        line.Sku,
        line.Unit,
        line.Quantity,
        line.UnitPrice,
        line.VatCategory,
        line.Id,
        line.OriginalInvoiceLineId,
        line.TaxExemptionReasonCode,
        line.TaxExemptionReason);
}

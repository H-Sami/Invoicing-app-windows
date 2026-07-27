using MHC.Invoicing.Domain.Invoices;

namespace MHC.Invoicing.Application.Workflows;

public sealed record ResumableDraft(
    Guid Id,
    InvoiceDocumentType DocumentType,
    DateOnly BusinessDate,
    string CustomerName,
    int LineCount,
    DateTimeOffset UpdatedAtUtc);

public interface IResumeDraftSource
{
    Task<IReadOnlyList<ResumableDraft>> LoadAsync(
        int limit,
        CancellationToken cancellationToken = default);
}

public sealed class ResumeDraftWorkflow(IResumeDraftSource source)
{
    public async Task<IReadOnlyList<ResumableDraft>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ResumableDraft> drafts = await source.LoadAsync(100, cancellationToken)
            .ConfigureAwait(false);
        return drafts
            .OrderByDescending(draft => draft.UpdatedAtUtc)
            .ThenByDescending(draft => draft.Id)
            .ToArray();
    }

    public static Guid Select(IReadOnlyList<ResumableDraft> displayedDrafts, Guid draftId)
    {
        ArgumentNullException.ThrowIfNull(displayedDrafts);
        return displayedDrafts.Any(draft => draft.Id == draftId)
            ? draftId
            : throw new KeyNotFoundException($"Draft {draftId} is no longer available.");
    }
}

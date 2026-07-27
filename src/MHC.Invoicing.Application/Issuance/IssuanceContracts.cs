using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Application.Issuance;

public interface IDocumentSerialGenerator
{
    DocumentSerial Create();
}

public sealed class DocumentSerialGenerator : IDocumentSerialGenerator
{
    public DocumentSerial Create() => DocumentSerial.Create();
}

public sealed record IssueSaleRequest
{
    public IssueSaleRequest(Guid draftId, int expectedDraftRevision)
    {
        if (draftId == Guid.Empty)
        {
            throw new ArgumentException("A persisted draft ID is required.", nameof(draftId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expectedDraftRevision);
        DraftId = draftId;
        ExpectedDraftRevision = expectedDraftRevision;
    }

    public Guid DraftId { get; }
    public int ExpectedDraftRevision { get; }
}

public sealed record IssueCreditNoteRequest
{
    public IssueCreditNoteRequest(Guid draftId, int expectedDraftRevision)
    {
        if (draftId == Guid.Empty)
        {
            throw new ArgumentException("A persisted draft ID is required.", nameof(draftId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(expectedDraftRevision);
        DraftId = draftId;
        ExpectedDraftRevision = expectedDraftRevision;
    }

    public Guid DraftId { get; }
    public int ExpectedDraftRevision { get; }
}

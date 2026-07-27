using MHC.Invoicing.Application.Workflows;
using MHC.Invoicing.Domain.Invoices;

namespace MHC.Invoicing.Application.Tests.Workflows;

public sealed class ResumeDraftWorkflowTests
{
    [Fact]
    public async Task LoadShowsMostRecentlyUpdatedDraftsAndRecoversSelectedIdentity()
    {
        Guid older = Guid.Parse("019824f5-1ac0-7000-8000-000000000001");
        Guid newer = Guid.Parse("019824f5-1ac0-7000-8000-000000000002");
        FakeSource source = new([
            new ResumableDraft(older, InvoiceDocumentType.TaxInvoice, new DateOnly(2026, 7, 22), "Older", 1, new DateTimeOffset(2026, 7, 22, 8, 0, 0, TimeSpan.Zero)),
            new ResumableDraft(newer, InvoiceDocumentType.CreditNote, new DateOnly(2026, 7, 23), "Newer", 2, new DateTimeOffset(2026, 7, 23, 9, 0, 0, TimeSpan.Zero)),
        ]);
        ResumeDraftWorkflow workflow = new(source);

        IReadOnlyList<ResumableDraft> drafts = await workflow.LoadAsync(TestContext.Current.CancellationToken);
        Guid selected = ResumeDraftWorkflow.Select(drafts, newer);

        Assert.Equal([newer, older], drafts.Select(draft => draft.Id));
        Assert.Equal(newer, selected);
    }

    [Fact]
    public void SelectRejectsAStaleDraftThatIsNoLongerInThePicker()
    {
        Assert.Throws<KeyNotFoundException>(() =>
            ResumeDraftWorkflow.Select([], Guid.Parse("019824f5-1ac0-7000-8000-000000000003")));
    }

    private sealed class FakeSource(IReadOnlyList<ResumableDraft> drafts) : IResumeDraftSource
    {
        public Task<IReadOnlyList<ResumableDraft>> LoadAsync(
            int limit,
            CancellationToken cancellationToken = default) => Task.FromResult(drafts);
    }
}

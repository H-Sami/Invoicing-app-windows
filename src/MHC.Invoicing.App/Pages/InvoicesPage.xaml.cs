using System.Globalization;
using MHC.Invoicing.App.Documents;
using MHC.Invoicing.App.Localization;
using MHC.Invoicing.App.Workflows;
using MHC.Invoicing.Application.Invoices;
using MHC.Invoicing.Application.Maintenance;
using MHC.Invoicing.Application.Persistence;

using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Repositories;
using MHC.Invoicing.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;



namespace MHC.Invoicing.App.Pages;

public sealed partial class InvoicesPage : Page
{
    private InvoiceHistoryWorkflow? _workflow;

    public InvoicesPage()
    {
        InitializeComponent();
        FlowDirection = LocalizationState.Language == "ar-SA" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    private static string L(string key) => LocalizationState.GetString(key);

    private static string F(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, L(key), values);

    private InvoiceSummary? SelectedInvoice => (InvoiceList.SelectedItem as InvoiceRow)?.Invoice;

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        AppComposition? services = ((App)Microsoft.UI.Xaml.Application.Current).Services;
        if (services is null)
        {
            SetStatus(L("DatabaseOpenFailure.Message"));
            return;
        }

        InvoiceHistoryDataSource dataSource = new(services.ConnectionString, ApplicationMaintenanceGate.Shared);
        _workflow = new InvoiceHistoryWorkflow(dataSource, new CanonicalInvoicePdfActions(this));
        await RefreshAsync(null);
    }

    private async void InvoiceSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        await RefreshAsync(args.QueryText);

    private void InvoiceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool selected = SelectedInvoice is not null;
        PreviewButton.IsEnabled = selected;
        PrintButton.IsEnabled = selected;
        ExportButton.IsEnabled = selected;
        DuplicateButton.IsEnabled = selected;
        CreditNoteButton.IsEnabled = selected && !SelectedInvoice!.IsVoided &&
            SelectedInvoice.DocumentType == MHC.Invoicing.Domain.Invoices.InvoiceDocumentType.TaxInvoice;
        VoidButton.IsEnabled = selected && !SelectedInvoice!.IsVoided;
    }

    private void InvoiceList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (!ReferenceEquals(sender, InvoiceList))
        {
            return;
        }

        if (args.ItemContainer is ListViewItem container && args.Item is InvoiceRow row)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(container, row.AccessibleName);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                container,
                $"Invoices.Row.{row.Id:D}");
        }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e) =>
        await RunSelectedAsync((workflow, id) => workflow.PreviewAsync(id), L("InvoicePreviewSuccess.Message"));

    private async void Print_Click(object sender, RoutedEventArgs e) =>
        await RunSelectedAsync((workflow, id) => workflow.PrintAsync(id), L("InvoicePrintSuccess.Message"));

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        InvoiceSummary? selected = SelectedInvoice;
        if (_workflow is null || selected is null)
        {
            return;
        }

        try
        {
            bool saved = await _workflow.ExportAsync(selected.Id);
            SetStatus(L(saved ? "InvoiceExportSuccess.Message" : "InvoiceExportCancelled.Message"));
        }
        catch (Exception exception)
        {
            SetStatus(UserFacingError.Localize(exception));
        }
    }

    private async void Duplicate_Click(object sender, RoutedEventArgs e) =>
        await RunSelectedAsync(async (workflow, id) =>
        {
            VersionedDraft draft = await workflow.DuplicateAsDraftAsync(id);
            ((App)Microsoft.UI.Xaml.Application.Current).MainWindow?.OpenInvoiceEditor(draft.Draft.Id);
        }, successMessage: null);

    private async void CreditNote_Click(object sender, RoutedEventArgs e) =>
        await RunSelectedAsync(async (workflow, id) =>
        {
            VersionedDraft draft = await workflow.CreateCreditNoteDraftAsync(id);
            ((App)Microsoft.UI.Xaml.Application.Current).MainWindow?.OpenInvoiceEditor(draft.Draft.Id);
        }, successMessage: null);

    private async void Void_Click(object sender, RoutedEventArgs e)
    {
        InvoiceSummary? selected = SelectedInvoice;
        if (_workflow is null || selected is null)
        {
            return;
        }

        TextBox reason = new() { Header = L("VoidReason.Header"), MaxLength = 1000 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reason, L("VoidReason.AutomationProperties.Name"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(reason, "Invoices.VoidReason");
        TextBox operatorName = new() { Header = L("VoidOperator.Header"), MaxLength = 200 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(operatorName, L("VoidOperator.AutomationProperties.Name"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(operatorName, "Invoices.VoidOperator");
        ContentDialog dialog = new()
        {
            Title = L("VoidConfirmation.Title"),
            Content = new StackPanel { Spacing = 12, Children = { reason, operatorName } },
            PrimaryButtonText = L("VoidConfirmation.PrimaryButtonText"),
            CloseButtonText = L("CommonCancel.Content"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            FlowDirection = FlowDirection,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _workflow.VoidAsync(selected.Id, reason.Text, operatorName.Text);
            await RefreshAsync(InvoiceSearch.Text);
            SetStatus(L("InvoiceVoidSuccess.Message"));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            SetStatus(UserFacingError.Localize(exception));
        }
    }

    private async Task RefreshAsync(string? searchText)
    {
        if (_workflow is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<InvoiceSummary> results = await _workflow.SearchAsync(searchText);
            InvoiceList.ItemsSource = results.Select(invoice => new InvoiceRow(invoice)).ToArray();
            EmptyState.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            InvoiceList.Visibility = results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            SetStatus(string.Empty);
        }
        catch (Exception exception)
        {
            SetStatus(UserFacingError.Localize(exception));
        }
    }

    private async Task RunSelectedAsync(
        Func<InvoiceHistoryWorkflow, Guid, Task> operation,
        string? successMessage)
    {
        InvoiceSummary? selected = SelectedInvoice;
        if (_workflow is null || selected is null)
        {
            return;
        }

        try
        {
            await operation(_workflow, selected.Id);
            if (successMessage is not null)
            {
                SetStatus(successMessage);
            }
        }
        catch (Exception exception)
        {
            SetStatus(UserFacingError.Localize(exception));
        }
    }

    private void SetStatus(string value) => OperationStatus.Text = value;

    private sealed record InvoiceRow(InvoiceSummary Invoice)
    {
        public string PublicNumber => Invoice.PublicNumber;

        public string CustomerName => LocalizationState.Language == "en-US" &&
            !string.IsNullOrWhiteSpace(Invoice.CustomerNameEnglish)
                ? Invoice.CustomerNameEnglish
                : Invoice.CustomerNameArabic;

        public Guid Id => Invoice.Id;

        public string DocumentTypeText => L(Invoice.DocumentType ==
            MHC.Invoicing.Domain.Invoices.InvoiceDocumentType.CreditNote
                ? "DocumentType.CreditNote"
                : "DocumentType.TaxInvoice");

        public string BusinessDateText => Invoice.BusinessDate.ToString(
            "d", DisplayCulture.Gregorian(LocalizationState.Language));

        public string GrandTotalText => F(
            "MoneySar.Format",
            Invoice.GrandTotal.Riyals.ToString("N2", CultureInfo.GetCultureInfo(LocalizationState.Language)));

        public string AccessibleName => F(
            "DocumentRow.AutomationNameFormat",
            DocumentTypeText,
            PublicNumber,
            CustomerName,
            BusinessDateText,
            GrandTotalText);
    }

    private sealed class InvoiceHistoryDataSource(
        string connectionString,
        IApplicationWorkGate workGate) : IInvoiceHistoryDataSource
    {
        public async Task<IReadOnlyList<InvoiceSummary>> SearchAsync(
            string? searchText,
            DateOnly? fromBusinessDate,
            DateOnly? toBusinessDate,
            int limit,
            CancellationToken cancellationToken = default)
        {
            await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
            await using MhcDbContext context = CreateContext(connectionString);
            return await new GetInvoiceHistory(new InvoiceRepository(context)).ExecuteAsync(
                searchText, fromBusinessDate, toBusinessDate, limit, cancellationToken);
        }

        public async Task<InvoiceSummary?> GetSummaryAsync(
            Guid invoiceId,
            CancellationToken cancellationToken = default)
        {
            await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
            await using MhcDbContext context = CreateContext(connectionString);
            return await new InvoiceRepository(context).GetSummaryAsync(invoiceId, cancellationToken);
        }

        public async Task<InvoiceSnapshot?> GetSnapshotAsync(
            Guid invoiceId,
            CancellationToken cancellationToken = default)
        {
            await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
            await using MhcDbContext context = CreateContext(connectionString);
            return await new InvoiceRepository(context).GetSnapshotAsync(invoiceId, cancellationToken);
        }

        public async Task<InvoiceDocument?> GetDocumentAsync(
            Guid invoiceId,
            CancellationToken cancellationToken = default)
        {
            await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
            await using MhcDbContext context = CreateContext(connectionString);
            return await new GetInvoiceDocument(new InvoiceRepository(context)).ExecuteAsync(invoiceId, cancellationToken);
        }

        public async Task<VersionedDraft> DuplicateAsDraftAsync(
            Guid invoiceId,
            CancellationToken cancellationToken = default)
        {
            await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
            await using MhcDbContext context = CreateContext(connectionString);
            return await new DuplicateInvoiceAsDraft(
                new InvoiceRepository(context),
                new DraftRepository(context),
                new SaudiClock()).ExecuteAsync(invoiceId, cancellationToken);
        }

        public async Task<VersionedDraft> CreateCreditNoteDraftAsync(
            Guid invoiceId,
            CancellationToken cancellationToken = default)
        {
            await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
            await using MhcDbContext context = CreateContext(connectionString);
            return await new CreateCreditNoteAsDraft(
                new InvoiceRepository(context),
                new DraftRepository(context),
                new SaudiClock()).ExecuteAsync(invoiceId, cancellationToken);
        }

        public async Task<InvoiceVoidInfo> VoidAsync(
            Guid invoiceId,
            string reason,
            string operatorName,
            CancellationToken cancellationToken = default)
        {
            await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
            await using MhcDbContext context = CreateContext(connectionString);
            return await new VoidInvoice(new InvoiceRepository(context), new SaudiClock()).ExecuteAsync(
                invoiceId, reason, operatorName, cancellationToken);
        }

        private static MhcDbContext CreateContext(string value)
        {
            DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
                .UseSqlite(value)
                .Options;
            return new MhcDbContext(options);
        }
    }

}

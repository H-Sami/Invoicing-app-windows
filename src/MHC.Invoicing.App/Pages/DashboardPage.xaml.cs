using System.Globalization;
using MHC.Invoicing.App.Localization;
using MHC.Invoicing.App.Workflows;
using MHC.Invoicing.Application.Maintenance;
using MHC.Invoicing.Application.Workflows;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Repositories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MHC.Invoicing.App.Pages;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        FlowDirection = LocalizationState.Language == "ar-SA"
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
    }

    private static string L(string key) => LocalizationState.GetString(key);

    private async void DashboardPage_Loaded(object sender, RoutedEventArgs e)
    {
        DashboardLoadError.Visibility = Visibility.Collapsed;
        try
        {
            App app = (App)Microsoft.UI.Xaml.Application.Current;
            AppComposition services = app.Services
                ?? throw new InvalidOperationException(L("ApplicationServicesUnavailable.Message"));
            DashboardSnapshot dashboard;
            await using (IAsyncDisposable work = await ApplicationMaintenanceGate.Shared.EnterWorkAsync())
            await using (MhcDbContext context = ScopedPersistence.CreateContext(services.ConnectionString))
            {
                DateTimeOffset saudiNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3));
                DateOnly monthStart = new(saudiNow.Year, saudiNow.Month, 1);
                dashboard = await new InvoiceRepository(context).GetDashboardAsync(monthStart, 5);
            }

            CultureInfo culture = DisplayCulture.Gregorian(LocalizationState.Language);
            InvoiceCountValue.Text = dashboard.InvoiceCount.ToString(culture);
            SalesValue.Text = string.Concat(
                dashboard.TotalSales.Riyals.ToString("N2", culture),
                " ",
                MHC.Invoicing.Domain.ValueObjects.Money.Currency);
            RecentInvoicesList.ItemsSource = dashboard.RecentInvoices
                .Select(invoice => new RecentInvoiceChoice(invoice, culture))
                .ToArray();
            bool hasRecent = dashboard.RecentInvoices.Count > 0;
            EmptyRecentPanel.Visibility = hasRecent ? Visibility.Collapsed : Visibility.Visible;
            RecentInvoicesList.Visibility = hasRecent ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Dashboard load failed: {exception}");
            InvoiceCountValue.Text = "—";
            SalesValue.Text = "—";
            RecentInvoicesList.Visibility = Visibility.Collapsed;
            EmptyRecentPanel.Visibility = Visibility.Collapsed;
            DashboardLoadError.Visibility = Visibility.Visible;
        }
    }

    private void NewInvoice_Click(object sender, RoutedEventArgs e) =>
        ((App)Microsoft.UI.Xaml.Application.Current).MainWindow?.OpenInvoiceEditor();

    private async void ResumeDraft_Click(object sender, RoutedEventArgs e)
    {
        App app = (App)Microsoft.UI.Xaml.Application.Current;
        AppComposition services = app.Services
            ?? throw new InvalidOperationException(L("ApplicationServicesUnavailable.Message"));
        IReadOnlyList<ResumableDraft> drafts;
        await using (IAsyncDisposable work = await ApplicationMaintenanceGate.Shared.EnterWorkAsync())
        await using (MHC.Invoicing.Infrastructure.Persistence.MhcDbContext context =
            ScopedPersistence.CreateContext(services.ConnectionString))
        {
            drafts = await new ResumeDraftWorkflow(new DraftRepository(context)).LoadAsync();
        }

        if (drafts.Count == 0)
        {
            ContentDialog empty = new()
            {
                Title = L("ResumeDraftDialog.Title"),
                Content = L("ResumeDraftDialog.EmptyMessage"),
                CloseButtonText = L("CommonClose.Content"),
                XamlRoot = XamlRoot,
                FlowDirection = FlowDirection,
            };
            await empty.ShowAsync();
            return;
        }

        ListView choices = new()
        {
            ItemsSource = drafts.Select(draft => new DraftChoice(draft)).ToArray(),
            SelectionMode = ListViewSelectionMode.Single,
            SelectedIndex = 0,
            MinWidth = 440,
            MaxHeight = 360,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(choices, "Dashboard.ResumeDraftChoices");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(choices, L("ResumeDraftDialog.ListAutomationName"));
        ContentDialog dialog = new()
        {
            Title = L("ResumeDraftDialog.Title"),
            Content = choices,
            PrimaryButtonText = L("ResumeDraftDialog.PrimaryButtonText"),
            CloseButtonText = L("CommonCancel.Content"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            FlowDirection = FlowDirection,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && choices.SelectedItem is DraftChoice selected)
        {
            Guid draftId = ResumeDraftWorkflow.Select(drafts, selected.Draft.Id);
            app.MainWindow?.OpenInvoiceEditor(draftId);
        }
    }

    private sealed record DraftChoice(ResumableDraft Draft)
    {
        public override string ToString()
        {
            CultureInfo culture = DisplayCulture.Gregorian(LocalizationState.Language);
            return string.Concat(
                Draft.CustomerName,
                " • ",
                Draft.BusinessDate.ToString("d", culture),
                " • ",
                Draft.LineCount.ToString(culture),
                " ",
                L("ResumeDraftDialog.LinesSuffix"));
        }
    }

    private sealed record RecentInvoiceChoice(
        MHC.Invoicing.Application.Persistence.InvoiceSummary Invoice,
        CultureInfo Culture)
    {
        public string PublicNumber => Invoice.PublicNumber;

        public string TypeAndStatus => Invoice.IsVoided
            ? string.Concat(L($"DocumentType.{Invoice.DocumentType}"), " - ", L("InvoiceStatus.Voided"))
            : L($"DocumentType.{Invoice.DocumentType}");

        public string CustomerName => Invoice.CustomerNameArabic;

        public string BusinessDate => Invoice.BusinessDate.ToString("d", Culture);

        public string Amount => string.Concat(
            (Invoice.DocumentType == InvoiceDocumentType.CreditNote
                ? -Invoice.GrandTotal.Riyals
                : Invoice.GrandTotal.Riyals).ToString("N2", Culture),
            " ",
            MHC.Invoicing.Domain.ValueObjects.Money.Currency);

        public string AccessibleName => string.Join(", ", PublicNumber, TypeAndStatus, CustomerName, BusinessDate, Amount);
    }
}

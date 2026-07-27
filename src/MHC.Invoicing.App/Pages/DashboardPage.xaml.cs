using System.Globalization;
using MHC.Invoicing.App.Localization;
using MHC.Invoicing.App.Workflows;
using MHC.Invoicing.Application.Maintenance;
using MHC.Invoicing.Application.Workflows;
using MHC.Invoicing.Infrastructure.Repositories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MHC.Invoicing.App.Pages;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
    }

    private static string L(string key) => LocalizationState.GetString(key);

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
}

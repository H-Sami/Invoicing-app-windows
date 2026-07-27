using MHC.Invoicing.App.Documents;
using MHC.Invoicing.App.Localization;
using MHC.Invoicing.App.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace MHC.Invoicing.App;

public sealed partial class MainWindow : Window
{
#if LOCAL_QA
    internal const string WindowTitle = "MHC Invoices V4 - LOCAL QA";
#else
    internal const string WindowTitle = "MHC Invoices";
#endif
    private Guid? _pendingDraftId;

    internal WebView2InvoiceDocumentService DocumentService { get; }

    public MainWindow(ElementTheme requestedTheme = ElementTheme.Default)
    {
        InitializeComponent();
        Title = WindowTitle;
        DocumentService = new WebView2InvoiceDocumentService(DocumentRendererWebView);
        Closed += (_, _) => DocumentService.Dispose();
        WindowRoot.FlowDirection = LocalizationState.Language == "ar-SA"
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        WindowRoot.Language = LocalizationState.Language;
        AutomationProperties.SetItemStatus(
            PrimaryNavigation,
            $"Language={WindowRoot.Language};FlowDirection={WindowRoot.FlowDirection}");
        WindowRoot.RequestedTheme = requestedTheme;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1280, 820));
        PrimaryNavigation.SelectedItem = DashboardItem;
        NavigateTo(typeof(DashboardPage));
    }

    private void PrimaryNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string destination)
        {
            return;
        }

        Type pageType = destination switch
        {
            "dashboard" => typeof(DashboardPage),
            "new-invoice" => typeof(InvoiceEditorPage),
            "invoices" => typeof(InvoicesPage),
            "customers" => typeof(CustomersPage),
            "items" => typeof(ItemsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DashboardPage),
        };
        if (pageType == typeof(InvoiceEditorPage))
        {
            Guid? draftId = _pendingDraftId;
            _pendingDraftId = null;
            ContentFrame.Navigate(pageType, draftId);
            return;
        }

        NavigateTo(pageType);
    }

    internal void OpenInvoiceEditor(Guid? draftId = null)
    {
        _pendingDraftId = draftId;
        if (ReferenceEquals(PrimaryNavigation.SelectedItem, InvoiceEditorItem))
        {
            Guid? pending = _pendingDraftId;
            _pendingDraftId = null;
            ContentFrame.Navigate(typeof(InvoiceEditorPage), pending);
        }
        else
        {
            PrimaryNavigation.SelectedItem = InvoiceEditorItem;
        }
    }

    internal void OpenSettings()
    {
        if (ReferenceEquals(PrimaryNavigation.SelectedItem, SettingsItem))
        {
            NavigateTo(typeof(SettingsPage));
        }
        else
        {
            PrimaryNavigation.SelectedItem = SettingsItem;
        }
    }

    private void NavigateTo(Type pageType)
    {
        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}

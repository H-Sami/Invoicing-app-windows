using System.Globalization;
using MHC.Invoicing.App.Documents;
using MHC.Invoicing.App.Localization;
using MHC.Invoicing.App.Workflows;
using MHC.Invoicing.Application.Customers;
using MHC.Invoicing.Application.Drafts;
using MHC.Invoicing.Application.Items;
using MHC.Invoicing.Application.Maintenance;
using MHC.Invoicing.Application.Persistence;

using MHC.Invoicing.Application.Workflows;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;
using MHC.Invoicing.Infrastructure.Persistence;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace MHC.Invoicing.App.Pages;

public sealed partial class InvoiceEditorPage : Page, IDisposable
{
    private CancellationTokenSource? _lifetime;
    private InvoiceEditorWorkflow? _workflow;
    private bool _rendering;
    private Guid? _draftId;

    public InvoiceEditorPage()
    {
        InitializeComponent();
        FlowDirection = LocalizationState.Language == "ar-SA" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        StatusBar.Title = L("AutosaveStatus.Title");
        StatusBar.Message = L("AutosaveInitializing.Message");
        ValidationBar.Title = L("ValidationErrors.Title");
        ProfileNotReadyBar.Title = L("ProfileNotReady.Title");
        ProfileNotReadyBar.Message = L("ProfileNotReady.Message");
        SetHeader(CustomerNameArabic, "CustomerSnapshotNameArabic.Header");
        SetHeader(CustomerNameEnglish, "CustomerSnapshotNameEnglish.Header");
        SetHeader(CustomerVatNumber, "CustomerSnapshotVat.Header");
        SetHeader(CustomerCommercialRegistration, "CustomerSnapshotCr.Header");
        SetHeader(CustomerAddress, "CustomerSnapshotAddress.Header");
    }

    private static string L(string key) => LocalizationState.GetString(key);

    private static string F(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, L(key), values);

    private static void SetHeader(TextBox field, string key)
    {
        field.Header = L(key);
        AutomationProperties.SetName(field, L(key));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _draftId = e.Parameter as Guid?;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _lifetime = new CancellationTokenSource();
        try
        {
            AppComposition services = ((App)Microsoft.UI.Xaml.Application.Current).Services
                ?? throw new InvalidOperationException(L("ApplicationServicesUnavailable.Message"));
            ApplicationMaintenanceGate gate = ApplicationMaintenanceGate.Shared;
            ScopedDraftRepository drafts = new(services.ConnectionString, gate);
            ScopedCustomerRepository customers = new(services.ConnectionString, gate);
            ScopedCatalogItemRepository items = new(services.ConnectionString, gate);
            ScopedInvoiceRepository invoices = new(services.ConnectionString, gate);
            _workflow = new InvoiceEditorWorkflow(
                drafts,
                new DraftAutosaveService(drafts, new SqliteTransientPersistenceErrorPolicy()),
                new LookupAdapter(customers, items),
                new IssuanceAdapter(services.Issuance, gate),
                new InvoiceEditorDocumentAdapter(invoices, new CanonicalInvoicePdfActions(this)),
                new ScopedInvoiceEditorCompanyProfile(services.ConnectionString, gate));
            await _workflow.InitializeAsync(_draftId, _lifetime.Token).ConfigureAwait(true);
            BusinessDatePicker.Date = new DateTimeOffset(
                _workflow.State.Draft.BusinessDate.ToDateTime(TimeOnly.MinValue),
                TimeSpan.Zero);
            RenderState();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowFailure(exception);
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        _workflow = null;
        GC.SuppressFinalize(this);
    }

    private async void CustomerSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || _workflow is null || _lifetime is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<CustomerSuggestion> results = await _workflow.SearchCustomersAsync(
                sender.Text,
                _lifetime.Token).ConfigureAwait(true);
            sender.ItemsSource = results.Select(customer => new CustomerChoice(customer)).ToArray();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowFailure(exception);
        }
    }

    private async void CustomerSearch_SuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not CustomerChoice choice || _workflow is null || _lifetime is null)
        {
            return;
        }

        await RunWorkflowAsync(() => _workflow.SelectCustomerAsync(choice.Customer, _lifetime.Token));
        sender.Text = choice.DisplayName;
    }

    private async void CatalogSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || _workflow is null || _lifetime is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<CatalogItemSuggestion> results = await _workflow.SearchCatalogAsync(
                sender.Text,
                _lifetime.Token).ConfigureAwait(true);
            sender.ItemsSource = results.Select(item => new CatalogChoice(item)).ToArray();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowFailure(exception);
        }
    }

    private async void CatalogSearch_SuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not CatalogChoice choice || _workflow is null || _lifetime is null)
        {
            return;
        }

        await RunWorkflowAsync(() => _workflow.AddCatalogItemAsync(choice.Item.Id, _lifetime.Token));
        sender.Text = string.Empty;
    }

    private async void BusinessDatePicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_rendering || !args.NewDate.HasValue || _workflow is null || _lifetime is null)
        {
            return;
        }

        await RunWorkflowAsync(() => _workflow.SetBusinessDateAsync(
            DateOnly.FromDateTime(args.NewDate.Value.Date),
            _lifetime.Token));
    }

    private async void SaveCustomerSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow is null || _lifetime is null)
        {
            return;
        }

        DraftParty customer = new(
            CustomerNameArabic.Text,
            CustomerNameEnglish.Text,
            CustomerVatNumber.Text,
            CustomerCommercialRegistration.Text,
            CustomerAddress.Text);
        await RunWorkflowAsync(() => _workflow.SetCustomerSnapshotAsync(customer, _lifetime.Token));
    }

    private async void AddOneOffLine_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow is null || _lifetime is null)
        {
            return;
        }

        TextBox description = DialogTextBox("OneOffDescription.Header", "OneOffLine.Description");
        TextBox sku = DialogTextBox("OneOffSku.Header", "OneOffLine.Sku");
        TextBox unit = DialogTextBox("OneOffUnit.Header", "OneOffLine.Unit");
        NumberBox quantity = new() { Header = L("LineQuantity.Header"), Minimum = 0, Value = 1, SmallChange = 1 };
        AutomationProperties.SetName(quantity, L("LineQuantity.Header"));
        AutomationProperties.SetAutomationId(quantity, "OneOffLine.Quantity");
        NumberBox price = new() { Header = L("LineUnitPrice.Header"), Minimum = 0, Value = 0, SmallChange = 1 };
        AutomationProperties.SetName(price, L("LineUnitPrice.Header"));
        AutomationProperties.SetAutomationId(price, "OneOffLine.UnitPrice");
        ComboBox vat = new()
        {
            Header = L("LineVatCategory.Header"),
            ItemsSource = new[] { L("VatStandard.Content"), L("VatZeroRated.Content"), L("VatExempt.Content") },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(vat, L("LineVatCategory.Header"));
        AutomationProperties.SetAutomationId(vat, "OneOffLine.VatCategory");
        TextBox exemptionCode = DialogTextBox("LineExemptionCode.Header", "OneOffLine.ExemptionCode");
        TextBox exemptionReason = DialogTextBox("LineExemptionReason.Header", "OneOffLine.ExemptionReason");
        StackPanel fields = new()
        {
            Spacing = 10,
            MinWidth = 460,
            Children = { description, sku, unit, quantity, price, vat, exemptionCode, exemptionReason },
        };
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = L("AddOneOffLineDialog.Title"),
            Content = fields,
            PrimaryButtonText = L("AddOneOffLineDialog.PrimaryButtonText"),
            CloseButtonText = L("CommonCancel.Content"),
            DefaultButton = ContentDialogButton.Primary,
            FlowDirection = FlowDirection,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        decimal quantityValue = double.IsNaN(quantity.Value) ? 0m : (decimal)quantity.Value;
        decimal priceValue = double.IsNaN(price.Value) ? -1m : (decimal)price.Value;
        VatCategory category = (VatCategory)(vat.SelectedIndex + 1);
        await RunWorkflowAsync(() => _workflow.AddOneOffLineAsync(
            description.Text,
            sku.Text,
            unit.Text,
            quantityValue,
            Money.FromRiyals(priceValue),
            category,
            exemptionCode.Text,
            exemptionReason.Text,
            _lifetime.Token));
    }

    private static TextBox DialogTextBox(string key, string automationId)
    {
        TextBox field = new() { Header = L(key) };
        AutomationProperties.SetName(field, L(key));
        AutomationProperties.SetAutomationId(field, automationId);
        return field;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) =>
        ((App)Microsoft.UI.Xaml.Application.Current).MainWindow?.OpenSettings();

    private async void IssueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow is null || _lifetime is null)
        {
            return;
        }

        bool isCreditNote = _workflow.State.Draft.DocumentType == InvoiceDocumentType.CreditNote;
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = L(isCreditNote ? "CreditNoteIssueConfirmation.Title" : "IssueConfirmation.Title"),
            Content = L(isCreditNote ? "CreditNoteIssueConfirmation.Message" : "IssueConfirmation.Message"),
            PrimaryButtonText = L(isCreditNote
                ? "CreditNoteIssueConfirmation.PrimaryButtonText"
                : "IssueConfirmation.PrimaryButtonText"),
            CloseButtonText = L("CommonCancel.Content"),
            DefaultButton = ContentDialogButton.Close,
            FlowDirection = FlowDirection,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        IssueButton.IsEnabled = false;
        StatusBar.IsOpen = true;
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = L(isCreditNote
            ? "CreditNoteIssuanceInProgress.Message"
            : "IssuanceInProgress.Message");
        await RunWorkflowAsync(async () =>
        {
            await _workflow.IssueAsync(true, _lifetime.Token).ConfigureAwait(true);
        });
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e) =>
        await RunDocumentActionAsync(() => _workflow!.PreviewAsync(_lifetime!.Token));

    private async void PrintButton_Click(object sender, RoutedEventArgs e) =>
        await RunDocumentActionAsync(() => _workflow!.PrintAsync(_lifetime!.Token));

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow is null || _lifetime is null)
        {
            return;
        }

        try
        {
            bool saved = await _workflow.ExportAsync(_lifetime.Token).ConfigureAwait(true);
            RenderState();
            StatusBar.IsOpen = true;
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Message = L(saved
                ? "InvoiceExportSuccess.Message"
                : "InvoiceExportCancelled.Message");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RenderState();
            ShowFailure(exception);
        }
    }

    private async Task RunDocumentActionAsync(Func<Task> operation)
    {
        if (_workflow is null || _lifetime is null)
        {
            return;
        }

        await RunWorkflowAsync(operation);
    }

    private async Task RunWorkflowAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(true);
            RenderState();
        }
        catch (OperationCanceledException) when (_lifetime?.IsCancellationRequested == true)
        {
        }
        catch (Exception exception)
        {
            RenderState();
            ShowFailure(exception);
        }
    }

    private void RenderState()
    {
        if (_workflow is null)
        {
            return;
        }

        _rendering = true;
        try
        {
            InvoiceEditorState state = _workflow.State;
            bool isCreditNote = state.Draft.DocumentType == InvoiceDocumentType.CreditNote;
            EditorTitle.Text = L(isCreditNote ? "CreditNoteEditorTitle.Text" : "EditorTitle.Text");
            EditorSubtitle.Text = L(isCreditNote ? "CreditNoteEditorSubtitle.Text" : "EditorSubtitle.Text");
            SelectedCustomerText.Text = state.Draft.Customer.Name;
            CustomerNameArabic.Text = state.Draft.Customer.Name;
            CustomerNameEnglish.Text = state.Draft.Customer.NameEnglish ?? string.Empty;
            CustomerVatNumber.Text = state.Draft.Customer.VatNumber ?? string.Empty;
            CustomerCommercialRegistration.Text = state.Draft.Customer.CommercialRegistration ?? string.Empty;
            CustomerAddress.Text = state.Draft.Customer.Address ?? string.Empty;
            DraftBadge.Text = state.IssuedInvoice is null
                ? L(isCreditNote ? "CreditNoteDraftBadge.Text" : "DraftBadge.Text")
                : F("IssuedBadge.Format", state.IssuedInvoice.PublicNumber);
            IssueButton.Content = L(isCreditNote ? "CreditNoteIssueAction.Content" : "EditorIssueAction.Content");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                IssueButton,
                L(isCreditNote
                    ? "CreditNoteIssueAction.AutomationProperties.Name"
                    : "EditorIssueAction.AutomationProperties.Name"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                PreviewButton,
                L(isCreditNote
                    ? "CreditNotePreviewAction.AutomationProperties.Name"
                    : "EditorPreviewAction.AutomationProperties.Name"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                PrintButton,
                L(isCreditNote
                    ? "CreditNotePrintAction.AutomationProperties.Name"
                    : "EditorPrintAction.AutomationProperties.Name"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                ExportButton,
                L(isCreditNote
                    ? "CreditNoteExportAction.AutomationProperties.Name"
                    : "EditorExportAction.AutomationProperties.Name"));
            SubtotalText.Text = FormatMoney(state.Subtotal);
            VatText.Text = FormatMoney(state.Vat);
            GrandTotalText.Text = FormatMoney(state.GrandTotal);
            StatusBar.Severity = state.SaveStatus switch
            {
                InvoiceEditorSaveStatus.Saved => InfoBarSeverity.Success,
                InvoiceEditorSaveStatus.Conflict or InvoiceEditorSaveStatus.Error => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational,
            };
            StatusBar.Message = state.SaveStatus switch
            {
                InvoiceEditorSaveStatus.Saved => F("AutosaveSaved.Format", state.Revision),
                InvoiceEditorSaveStatus.Saving => L("AutosaveSaving.Message"),
                InvoiceEditorSaveStatus.Conflict => L("AutosaveConflict.Message"),
                _ => L("AutosaveError.Message"),
            };
            ValidationBar.IsOpen = state.Errors.Count > 0;
            ValidationBar.Message = string.Join(" • ", state.Errors.Select(error => TranslateValidation(error)));
            ProfileNotReadyBar.IsOpen = !state.IsCompanyProfileReady && state.IssuedInvoice is null;
            IssueButton.IsEnabled = state.CanIssue && state.IssuedInvoice is null;
            bool issued = state.IssuedInvoice is not null;
            PreviewButton.IsEnabled = issued;
            PrintButton.IsEnabled = issued;
            ExportButton.IsEnabled = issued;
            CustomerSearch.IsEnabled = !issued;
            CustomerNameArabic.IsEnabled = !issued;
            CustomerNameEnglish.IsEnabled = !issued;
            CustomerVatNumber.IsEnabled = !issued;
            CustomerCommercialRegistration.IsEnabled = !issued;
            CustomerAddress.IsEnabled = !issued;
            SaveCustomerSnapshotButton.IsEnabled = !issued;
            CatalogSearch.IsEnabled = !issued;
            AddOneOffLineButton.IsEnabled = !issued;
            BusinessDatePicker.IsEnabled = !issued;
            RenderLines(state, issued);
        }
        finally
        {
            _rendering = false;
        }
    }

    private void RenderLines(InvoiceEditorState state, bool issued)
    {
        LinesPanel.Children.Clear();
        EmptyLinesText.Visibility = state.Draft.Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (InvoiceDraftLine line in state.Draft.Lines)
        {
            Border card = new()
            {
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["MhcCardBorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
            };
            StackPanel fields = new() { Spacing = 8 };
            fields.Children.Add(new TextBlock { Text = line.Description, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            fields.Children.Add(new TextBlock { Text = $"{line.Sku ?? L("LineWithoutSku.Text")} — {line.Unit}" });
            NumberBox quantity = new() { Header = L("LineQuantity.Header"), Value = (double)line.Quantity, Minimum = 0, SmallChange = 1, IsEnabled = !issued };
            AutomationProperties.SetName(quantity, F("LineQuantity.AutomationNameFormat", line.Description));
            AutomationProperties.SetAutomationId(quantity, $"InvoiceLine.{line.Id:D}.Quantity");
            NumberBox price = new() { Header = L("LineUnitPrice.Header"), Value = (double)line.UnitPrice.Riyals, Minimum = 0, SmallChange = 1, IsEnabled = !issued };
            AutomationProperties.SetName(price, F("LineUnitPrice.AutomationNameFormat", line.Description));
            AutomationProperties.SetAutomationId(price, $"InvoiceLine.{line.Id:D}.UnitPrice");
            ComboBox vat = new()
            {
                Header = L("LineVatCategory.Header"),
                ItemsSource = new[] { L("VatStandard.Content"), L("VatZeroRated.Content"), L("VatExempt.Content") },
                SelectedIndex = (int)line.VatCategory - 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = !issued,
            };
            AutomationProperties.SetName(vat, F("LineVatCategory.AutomationNameFormat", line.Description));
            AutomationProperties.SetAutomationId(vat, $"InvoiceLine.{line.Id:D}.VatCategory");
            TextBox exemptionCode = new() { Header = L("LineExemptionCode.Header"), Text = line.TaxExemptionReasonCode ?? string.Empty, IsEnabled = !issued };
            AutomationProperties.SetName(exemptionCode, F("LineExemptionCode.AutomationNameFormat", line.Description));
            AutomationProperties.SetAutomationId(exemptionCode, $"InvoiceLine.{line.Id:D}.ExemptionCode");
            TextBox exemptionReason = new() { Header = L("LineExemptionReason.Header"), Text = line.TaxExemptionReason ?? string.Empty, IsEnabled = !issued };
            AutomationProperties.SetName(exemptionReason, F("LineExemptionReason.AutomationNameFormat", line.Description));
            AutomationProperties.SetAutomationId(exemptionReason, $"InvoiceLine.{line.Id:D}.ExemptionReason");
            fields.Children.Add(quantity);
            fields.Children.Add(price);
            fields.Children.Add(vat);
            fields.Children.Add(exemptionCode);
            fields.Children.Add(exemptionReason);
            if (!issued)
            {
                StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
                Button save = new() { Content = L("LineSaveAction.Content") };
                AutomationProperties.SetName(save, F("LineSaveAction.AutomationNameFormat", line.Description));
                AutomationProperties.SetAutomationId(save, $"InvoiceLine.{line.Id:D}.Save");
                save.Click += async (_, _) => await SaveLineAsync(line.Id, quantity, price, vat, exemptionCode, exemptionReason);
                Button remove = new() { Content = L("LineRemoveAction.Content") };
                AutomationProperties.SetName(remove, F("LineRemoveAction.AutomationNameFormat", line.Description));
                AutomationProperties.SetAutomationId(remove, $"InvoiceLine.{line.Id:D}.Remove");
                remove.Click += async (_, _) => await RunWorkflowAsync(() => _workflow!.RemoveLineAsync(line.Id, _lifetime!.Token));
                actions.Children.Add(save);
                actions.Children.Add(remove);
                fields.Children.Add(actions);
            }

            card.Child = fields;
            LinesPanel.Children.Add(card);
        }
    }

    private Task SaveLineAsync(
        Guid lineId,
        NumberBox quantity,
        NumberBox price,
        ComboBox vat,
        TextBox exemptionCode,
        TextBox exemptionReason)
    {
        decimal quantityValue = double.IsNaN(quantity.Value) ? 0m : (decimal)quantity.Value;
        decimal priceValue = double.IsNaN(price.Value) ? -1m : (decimal)price.Value;
        VatCategory category = vat.SelectedIndex switch
        {
            0 => VatCategory.Standard15,
            1 => VatCategory.ZeroRated,
            2 => VatCategory.Exempt,
            _ => (VatCategory)0,
        };
        return RunWorkflowAsync(() => _workflow!.UpdateLineAsync(
            lineId,
            quantityValue,
            Money.FromRiyals(priceValue),
            category,
            exemptionCode.Text,
            exemptionReason.Text,
            _lifetime!.Token));
    }

    private void ShowFailure(Exception exception)
    {
        StatusBar.IsOpen = true;
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Message = UserFacingError.Localize(exception);
    }

    private static string FormatMoney(Money value) =>
        F("MoneySar.Format", value.Riyals.ToString("N2", CultureInfo.GetCultureInfo(LocalizationState.Language)));

    private static string TranslateValidation(InvoiceValidationError error) => error.Code switch
    {
        "required" when error.Field == "lines" => L("ValidationLinesRequired.Message"),
        "invalid" when error.Field.EndsWith("quantity", StringComparison.Ordinal) => L("ValidationQuantityInvalid.Message"),
        "invalid" when error.Field.EndsWith("unitPrice", StringComparison.Ordinal) => L("ValidationUnitPriceInvalid.Message"),
        "required" when error.Field.EndsWith("taxExemptionReason", StringComparison.Ordinal) => L("ValidationExemptionRequired.Message"),
        _ => L("UserError.InvalidInput.Message"),
    };

    private sealed record CustomerChoice(CustomerSuggestion Customer)
    {
        public string DisplayName
        {
            get
            {
                string name = LocalizationState.Language == "en-US" && !string.IsNullOrWhiteSpace(Customer.NameEnglish)
                    ? Customer.NameEnglish
                    : Customer.NameArabic;
                return string.IsNullOrWhiteSpace(Customer.VatNumber) ? name : $"{name} — {Customer.VatNumber}";
            }
        }
    }

    private sealed record CatalogChoice(CatalogItemSuggestion Item)
    {
        public string DisplayName
        {
            get
            {
                string name = LocalizationState.Language == "en-US" && !string.IsNullOrWhiteSpace(Item.NameEnglish)
                    ? Item.NameEnglish
                    : Item.NameArabic;
                return $"{name} — {FormatMoney(Item.DefaultUnitPrice)}";
            }
        }
    }

    private sealed class LookupAdapter(ICustomerRepository customers, ICatalogItemRepository items)
        : IInvoiceEditorLookup
    {
        private readonly SearchCatalogItems _searchItems = new(items);
        private readonly SearchCustomers _searchCustomers = new(customers);
        private readonly SelectCatalogItem _selectItem = new(items);

        public Task<IReadOnlyList<CustomerSuggestion>> SearchCustomersAsync(
            string? searchText,
            CancellationToken cancellationToken = default) =>
            _searchCustomers.ExecuteAsync(searchText, cancellationToken);

        public Task<IReadOnlyList<CatalogItemSuggestion>> SearchCatalogAsync(
            string? searchText,
            CancellationToken cancellationToken = default) =>
            _searchItems.ExecuteAsync(searchText, cancellationToken);

        public Task<InvoiceDraftLine> SelectCatalogItemAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            _selectItem.ExecuteAsync(id, cancellationToken);
    }

    private sealed class IssuanceAdapter(
        InvoiceIssuanceService issuance,
        IApplicationWorkGate workGate) : IInvoiceEditorIssuance
    {
        public async Task<IssuedInvoiceReference> IssueAsync(
            Guid draftId,
            int expectedRevision,
            InvoiceDocumentType documentType,
            CancellationToken cancellationToken = default)
        {
            await using IAsyncDisposable work = await workGate.EnterWorkAsync(cancellationToken);
            IssuedInvoice invoice = documentType switch
            {
                InvoiceDocumentType.TaxInvoice => await issuance.IssueSaleAsync(
                    new MHC.Invoicing.Application.Issuance.IssueSaleRequest(draftId, expectedRevision),
                    cancellationToken).ConfigureAwait(false),
                InvoiceDocumentType.CreditNote => await issuance.IssueCreditNoteAsync(
                    new MHC.Invoicing.Application.Issuance.IssueCreditNoteRequest(draftId, expectedRevision),
                    cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(documentType)),
            };
            return new IssuedInvoiceReference(invoice.Id, invoice.Number.ToString(), documentType);
        }
    }

}

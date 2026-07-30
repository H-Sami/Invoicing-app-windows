using System.Globalization;
using MHC.Invoicing.App.Localization;
using MHC.Invoicing.App.Workflows;
using MHC.Invoicing.Application.Items;
using MHC.Invoicing.Application.Maintenance;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Repositories;
using MHC.Invoicing.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MHC.Invoicing.App.Pages;

public sealed partial class ItemsPage : Page
{
    private readonly CustomerCatalogWorkflow<CatalogRow> _workflow;
    private bool _loaded;

    public ItemsPage()
    {
        InitializeComponent();
        FlowDirection = LocalizationState.Language == "ar-SA" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _workflow = new(ClassifyError);
        _workflow.StateChanged += Workflow_StateChanged;
        ApplyAccessibleText();
    }

    private static string Text(string arabic, string english) =>
        LocalizationState.Language == "ar-SA" ? arabic : english;

    private void ApplyAccessibleText()
    {
        AutomationProperties.SetName(SearchBox, Text("البحث في دليل الأصناف والخدمات", "Search item and service catalog"));
        AutomationProperties.SetName(AddButton, Text("إضافة صنف أو خدمة", "Add an item or service"));
        AutomationProperties.SetName(ArchivedToggle, Text("إظهار الأصناف المؤرشفة", "Show archived items"));
        ArchivedToggle.Header = Text("إظهار المؤرشف", "Show archived");
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        await ReloadAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _workflow.Cancel();
    }

    private async void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_loaded && args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            await ReloadAsync();
        }
    }

    private async void Archived_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            await ReloadAsync();
        }
    }

    private Task ReloadAsync() => _workflow.LoadAsync(SearchBox.Text, LoadRowsAsync);

    private void ItemsList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (!ReferenceEquals(sender, ItemsList))
        {
            return;
        }

        if (args.ItemContainer is ListViewItem container && args.Item is CatalogRow row)
        {
            AutomationProperties.SetAutomationId(container, $"Items.Row.{row.Id:D}");
        }
    }

    private async Task<IReadOnlyList<CatalogRow>> LoadRowsAsync(string query, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable work = await ApplicationMaintenanceGate.Shared.EnterWorkAsync(cancellationToken);
        await using MhcDbContext context = CreateContext();
        IReadOnlyList<VersionedCatalogItem> items = await new CatalogItemRepository(context).SearchAsync(
            query,
            ArchivedToggle.IsOn,
            100,
            cancellationToken);
        return items.Select(CatalogRow.From).ToArray();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        CatalogEdit? edit = await ShowEditorAsync(null);
        if (edit is null)
        {
            return;
        }

        await MutateAsync(async cancellationToken =>
        {
            await using IAsyncDisposable work = await ApplicationMaintenanceGate.Shared.EnterWorkAsync(cancellationToken);
            await using MhcDbContext context = CreateContext();
            CatalogItemRepository repository = new(context);
            await new CreateCatalogItem(repository, new SaudiClock()).ExecuteAsync(edit.ToCommand(), cancellationToken);
        }, Text("تمت إضافة الصنف أو الخدمة.", "Item or service added."));
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not CatalogRow row)
        {
            return;
        }

        CatalogEdit? edit = await ShowEditorAsync(row);
        if (edit is null)
        {
            return;
        }

        await MutateAsync(async cancellationToken =>
        {
            await using IAsyncDisposable work = await ApplicationMaintenanceGate.Shared.EnterWorkAsync(cancellationToken);
            await using MhcDbContext context = CreateContext();
            CatalogItemRepository repository = new(context);
            await new UpdateCatalogItem(repository, new SaudiClock()).ExecuteAsync(
                row.Id,
                row.Revision,
                edit.ToCommand(),
                cancellationToken);
        }, Text("تم تحديث الصنف أو الخدمة.", "Item or service updated."));
    }

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is CatalogRow row)
        {
            await SetArchivedAsync(row, true);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is CatalogRow row)
        {
            await SetArchivedAsync(row, false);
        }
    }

    private Task SetArchivedAsync(CatalogRow row, bool archived) => MutateAsync(async cancellationToken =>
    {
        await using IAsyncDisposable work = await ApplicationMaintenanceGate.Shared.EnterWorkAsync(cancellationToken);
        await using MhcDbContext context = CreateContext();
        CatalogItemRepository repository = new(context);
        SaudiClock clock = new();
        if (archived)
        {
            await new ArchiveCatalogItem(repository, clock).ExecuteAsync(row.Id, row.Revision, cancellationToken);
        }
        else
        {
            await new RestoreCatalogItem(repository, clock).ExecuteAsync(row.Id, row.Revision, cancellationToken);
        }
    }, archived ? Text("تمت أرشفة الصنف أو الخدمة.", "Item or service archived.") : Text("تمت استعادة الصنف أو الخدمة.", "Item or service restored."));

    private async Task MutateAsync(Func<CancellationToken, Task> mutation, string successMessage)
    {
        bool succeeded = await _workflow.MutateAsync(mutation, LoadRowsAsync);
        if (succeeded)
        {
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Message = successMessage;
            StatusBar.IsOpen = true;
        }
    }

    private async Task<CatalogEdit?> ShowEditorAsync(CatalogRow? row)
    {
        TextBox nameArabic = Field(Text("الاسم العربي (مطلوب)", "Arabic name (required)"), row?.NameArabic);
        TextBox nameEnglish = Field(Text("الاسم الإنجليزي", "English name"), row?.NameEnglish);
        TextBox sku = Field(Text("رمز الصنف", "SKU"), row?.Sku);
        TextBox unit = Field(Text("وحدة القياس", "Unit of measure"), row?.Unit ?? Text("وحدة", "unit"));
        NumberBox price = new()
        {
            Header = Text("السعر الافتراضي بالريال", "Default price in SAR"),
            Value = row is null ? 0d : Convert.ToDouble(row.DefaultUnitPrice.Riyals, CultureInfo.InvariantCulture),
            Minimum = 0,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        AutomationProperties.SetName(price, price.Header?.ToString() ?? string.Empty);
        ComboBox vat = new()
        {
            Header = Text("فئة الضريبة", "VAT category"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        vat.Items.Add(Text("قياسية 15٪", "Standard 15%"));
        vat.Items.Add(Text("نسبة صفرية", "Zero-rated"));
        vat.Items.Add(Text("معفاة", "Exempt"));
        vat.SelectedIndex = row is null ? 0 : VatIndex(row.VatCategory);
        AutomationProperties.SetName(vat, vat.Header?.ToString() ?? string.Empty);

        StackPanel fields = new() { Spacing = 10 };
        fields.Children.Add(nameArabic);
        fields.Children.Add(nameEnglish);
        fields.Children.Add(sku);
        fields.Children.Add(unit);
        fields.Children.Add(price);
        fields.Children.Add(vat);
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = row is null ? Text("صنف أو خدمة جديدة", "New item or service") : Text("تعديل الصنف أو الخدمة", "Edit item or service"),
            Content = new ScrollViewer { Content = fields, MaxHeight = 520 },
            PrimaryButtonText = Text("حفظ", "Save"),
            CloseButtonText = Text("إلغاء", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            FlowDirection = FlowDirection,
        };
        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        if (double.IsNaN(price.Value) || double.IsInfinity(price.Value) || price.Value < 0)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Message = Text("أدخل سعراً صحيحاً لا يقل عن صفر.", "Enter a valid price of zero or more.");
            StatusBar.IsOpen = true;
            return null;
        }

        return new CatalogEdit(
            nameArabic.Text,
            nameEnglish.Text,
            sku.Text,
            unit.Text,
            Convert.ToDecimal(price.Value, CultureInfo.InvariantCulture),
            VatFromIndex(vat.SelectedIndex));
    }

    private static TextBox Field(string header, string? value)
    {
        TextBox field = new() { Header = header, Text = value ?? string.Empty, MinWidth = 420 };
        AutomationProperties.SetName(field, header);
        return field;
    }

    private static int VatIndex(VatCategory category) => category switch
    {
        VatCategory.Standard15 => 0,
        VatCategory.ZeroRated => 1,
        VatCategory.Exempt => 2,
        _ => 0,
    };

    private static VatCategory VatFromIndex(int index) => index switch
    {
        1 => VatCategory.ZeroRated,
        2 => VatCategory.Exempt,
        _ => VatCategory.Standard15,
    };

    private static MhcDbContext CreateContext()
    {
        AppComposition composition = ((App)Microsoft.UI.Xaml.Application.Current).Services
            ?? throw new InvalidOperationException("Application services are not ready.");
        DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite(composition.ConnectionString)
            .Options;
        return new MhcDbContext(options);
    }

    private void Workflow_StateChanged(object? sender, EventArgs e)
    {
        CustomerCatalogWorkflowState<CatalogRow> state = _workflow.State;
        BusyIndicator.IsActive = state.IsBusy;
        ItemsList.ItemsSource = state.Items;
        ItemsList.Visibility = !state.IsBusy && state.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = !state.IsBusy && state.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddButton.IsEnabled = !state.IsBusy;
        if (state.ErrorKind != WorkflowErrorKind.None)
        {
            StatusBar.Severity = state.ErrorKind == WorkflowErrorKind.Conflict ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
            StatusBar.Message = state.ErrorKind switch
            {
                WorkflowErrorKind.Conflict => Text("تم تعديل السجل في نافذة أخرى. أعد تحميل النتائج وحاول مجدداً.", "This record changed elsewhere. Reload and try again."),
                WorkflowErrorKind.Validation => Text("تحقق من الاسم والوحدة والسعر ثم حاول مجدداً.", "Check the name, unit, and price, then try again."),
                _ => Text("تعذر إكمال العملية. حاول مرة أخرى.", "The operation could not be completed. Try again."),
            };
            StatusBar.IsOpen = true;
        }
    }

    private static WorkflowErrorKind ClassifyError(Exception exception) => exception switch
    {
        PersistenceConcurrencyException => WorkflowErrorKind.Conflict,
        ArgumentException or InvalidOperationException => WorkflowErrorKind.Validation,
        _ => WorkflowErrorKind.Unexpected,
    };

    private sealed record CatalogEdit(
        string NameArabic,
        string? NameEnglish,
        string? Sku,
        string Unit,
        decimal PriceRiyals,
        VatCategory VatCategory)
    {
        internal CatalogItemCommand ToCommand() => new(
            NameArabic,
            NameEnglish,
            Sku,
            Unit,
            Money.FromRiyals(PriceRiyals),
            VatCategory);
    }

    private sealed record CatalogRow(
        Guid Id,
        int Revision,
        string NameArabic,
        string? NameEnglish,
        string? Sku,
        string Unit,
        Money DefaultUnitPrice,
        VatCategory VatCategory,
        bool IsArchived)
    {
        public string DetailLine => string.Join(" • ", new[] { NameEnglish, Sku, Unit }.Where(value => !string.IsNullOrWhiteSpace(value)));

        public string PriceLine => DefaultUnitPrice.ToString("N2", CultureInfo.CurrentCulture);

        public Visibility ActiveVisibility => IsArchived ? Visibility.Collapsed : Visibility.Visible;

        public Visibility ArchivedVisibility => IsArchived ? Visibility.Visible : Visibility.Collapsed;

        public string EditLabel { get; } = Text("تعديل", "Edit");

        public string ArchiveLabel { get; } = Text("أرشفة", "Archive");

        public string RestoreLabel { get; } = Text("استعادة", "Restore");

        public string EditAutomationName { get; } = Text("تعديل الصنف أو الخدمة", "Edit item or service");

        public string ArchiveAutomationName { get; } = Text("أرشفة الصنف أو الخدمة", "Archive item or service");

        public string RestoreAutomationName { get; } = Text("استعادة الصنف أو الخدمة", "Restore item or service");

        internal static CatalogRow From(VersionedCatalogItem item) => new(
            item.CatalogItem.Id,
            item.Revision,
            item.CatalogItem.NameArabic,
            item.CatalogItem.NameEnglish,
            item.CatalogItem.Sku,
            item.CatalogItem.Unit.Value,
            item.CatalogItem.DefaultUnitPrice,
            item.CatalogItem.VatCategory,
            item.CatalogItem.IsArchived);
    }
}

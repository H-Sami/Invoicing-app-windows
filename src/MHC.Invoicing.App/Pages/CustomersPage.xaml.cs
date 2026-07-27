using MHC.Invoicing.App.Localization;
using MHC.Invoicing.App.Workflows;
using MHC.Invoicing.Application.Maintenance;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Customers;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Repositories;
using MHC.Invoicing.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MHC.Invoicing.App.Pages;

public sealed partial class CustomersPage : Page
{
    private readonly CustomerCatalogWorkflow<CustomerRow> _workflow;
    private bool _loaded;

    public CustomersPage()
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
        AutomationProperties.SetName(SearchBox, Text("البحث في دليل العملاء", "Search customer directory"));
        AutomationProperties.SetName(AddButton, Text("إضافة عميل جديد", "Add a new customer"));
        AutomationProperties.SetName(ArchivedToggle, Text("إظهار العملاء المؤرشفين", "Show archived customers"));
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

    private void CustomersList_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (!ReferenceEquals(sender, CustomersList))
        {
            return;
        }

        if (args.ItemContainer is ListViewItem container && args.Item is CustomerRow row)
        {
            AutomationProperties.SetAutomationId(container, $"Customers.Row.{row.Id:D}");
        }
    }

    private async Task<IReadOnlyList<CustomerRow>> LoadRowsAsync(string query, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable work = await ApplicationMaintenanceGate.Shared.EnterWorkAsync(cancellationToken);
        await using MhcDbContext context = CreateContext();
        CustomerRepository repository = new(context);
        IReadOnlyList<VersionedCustomer> customers = await repository.SearchAsync(
            query,
            ArchivedToggle.IsOn,
            100,
            cancellationToken);
        return customers.Select(CustomerRow.From).ToArray();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        CustomerEdit? edit = await ShowEditorAsync(null);
        if (edit is null)
        {
            return;
        }

        await MutateAsync(async cancellationToken =>
        {
            await using IAsyncDisposable work = await ApplicationMaintenanceGate.Shared.EnterWorkAsync(cancellationToken);
            await using MhcDbContext context = CreateContext();
            Customer customer = Customer.Create(
                edit.NameArabic,
                edit.NameEnglish,
                edit.VatNumber,
                edit.CommercialRegistration,
                edit.Address,
                edit.Phone,
                edit.Email,
                new SaudiClock().UtcNow);
            await new CustomerRepository(context).AddAsync(customer, cancellationToken);
        }, Text("تمت إضافة العميل.", "Customer added."));
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not CustomerRow row)
        {
            return;
        }

        CustomerEdit? edit = await ShowEditorAsync(row);
        if (edit is null)
        {
            return;
        }

        await MutateAsync(async cancellationToken =>
        {
            await using IAsyncDisposable work = await ApplicationMaintenanceGate.Shared.EnterWorkAsync(cancellationToken);
            await using MhcDbContext context = CreateContext();
            CustomerRepository repository = new(context);
            VersionedCustomer current = await GetRequiredAsync(repository, row.Id, cancellationToken);
            current.Customer.Update(
                edit.NameArabic,
                edit.NameEnglish,
                edit.VatNumber,
                edit.CommercialRegistration,
                edit.Address,
                edit.Phone,
                edit.Email,
                new SaudiClock().UtcNow);
            await repository.UpdateAsync(current.Customer, row.Revision, cancellationToken);
        }, Text("تم تحديث بيانات العميل.", "Customer updated."));
    }

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is CustomerRow row)
        {
            await SetArchivedAsync(row, true);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is CustomerRow row)
        {
            await SetArchivedAsync(row, false);
        }
    }

    private Task SetArchivedAsync(CustomerRow row, bool archived) => MutateAsync(async cancellationToken =>
    {
        await using IAsyncDisposable work = await ApplicationMaintenanceGate.Shared.EnterWorkAsync(cancellationToken);
        await using MhcDbContext context = CreateContext();
        CustomerRepository repository = new(context);
        VersionedCustomer current = await GetRequiredAsync(repository, row.Id, cancellationToken);
        if (archived)
        {
            current.Customer.Archive(new SaudiClock().UtcNow);
        }
        else
        {
            current.Customer.Restore(new SaudiClock().UtcNow);
        }

        await repository.UpdateAsync(current.Customer, row.Revision, cancellationToken);
    }, archived ? Text("تمت أرشفة العميل.", "Customer archived.") : Text("تمت استعادة العميل.", "Customer restored."));

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

    private async Task<CustomerEdit?> ShowEditorAsync(CustomerRow? row)
    {
        TextBox nameArabic = Field(Text("الاسم العربي (مطلوب)", "Arabic name (required)"), row?.NameArabic);
        TextBox nameEnglish = Field(Text("الاسم الإنجليزي", "English name"), row?.NameEnglish);
        TextBox vat = Field(Text("الرقم الضريبي (15 رقماً)", "VAT number (15 digits)"), row?.VatNumber);
        TextBox registration = Field(Text("السجل التجاري", "Commercial registration"), row?.CommercialRegistration);
        TextBox address = Field(Text("العنوان", "Address"), row?.Address);
        TextBox phone = Field(Text("الهاتف", "Phone"), row?.Phone);
        TextBox email = Field(Text("البريد الإلكتروني", "Email"), row?.Email);
        StackPanel fields = new() { Spacing = 10 };
        fields.Children.Add(nameArabic);
        fields.Children.Add(nameEnglish);
        fields.Children.Add(vat);
        fields.Children.Add(registration);
        fields.Children.Add(address);
        fields.Children.Add(phone);
        fields.Children.Add(email);

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = row is null ? Text("عميل جديد", "New customer") : Text("تعديل العميل", "Edit customer"),
            Content = new ScrollViewer { Content = fields, MaxHeight = 520 },
            PrimaryButtonText = Text("حفظ", "Save"),
            CloseButtonText = Text("إلغاء", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            FlowDirection = FlowDirection,
        };
        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary
            ? new CustomerEdit(nameArabic.Text, nameEnglish.Text, vat.Text, registration.Text, address.Text, phone.Text, email.Text)
            : null;
    }

    private static TextBox Field(string header, string? value)
    {
        TextBox field = new() { Header = header, Text = value ?? string.Empty, MinWidth = 420 };
        AutomationProperties.SetName(field, header);
        return field;
    }

    private static async Task<VersionedCustomer> GetRequiredAsync(
        CustomerRepository repository,
        Guid id,
        CancellationToken cancellationToken) =>
        await repository.GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException("Customer was not found.");

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
        CustomerCatalogWorkflowState<CustomerRow> state = _workflow.State;
        BusyIndicator.IsActive = state.IsBusy;
        CustomersList.ItemsSource = state.Items;
        CustomersList.Visibility = !state.IsBusy && state.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = !state.IsBusy && state.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddButton.IsEnabled = !state.IsBusy;
        if (state.ErrorKind != WorkflowErrorKind.None)
        {
            StatusBar.Severity = state.ErrorKind == WorkflowErrorKind.Conflict ? InfoBarSeverity.Warning : InfoBarSeverity.Error;
            StatusBar.Message = state.ErrorKind switch
            {
                WorkflowErrorKind.Conflict => Text("تم تعديل السجل في نافذة أخرى. أعد تحميل النتائج وحاول مجدداً.", "This record changed elsewhere. Reload and try again."),
                WorkflowErrorKind.Validation => Text("تحقق من البيانات المدخلة ثم حاول مجدداً.", "Check the entered data and try again."),
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

    private sealed record CustomerEdit(
        string NameArabic,
        string? NameEnglish,
        string? VatNumber,
        string? CommercialRegistration,
        string? Address,
        string? Phone,
        string? Email);

    private sealed record CustomerRow(
        Guid Id,
        int Revision,
        string NameArabic,
        string? NameEnglish,
        string? VatNumber,
        string? CommercialRegistration,
        string? Address,
        string? Phone,
        string? Email,
        bool IsArchived)
    {
        public string SecondaryLine => string.Join(" • ", new[] { NameEnglish, VatNumber, CommercialRegistration }.Where(value => !string.IsNullOrWhiteSpace(value)));

        public string ContactLine => string.Join(" • ", new[] { Phone, Email, Address }.Where(value => !string.IsNullOrWhiteSpace(value)));

        public Visibility ActiveVisibility => IsArchived ? Visibility.Collapsed : Visibility.Visible;

        public Visibility ArchivedVisibility => IsArchived ? Visibility.Visible : Visibility.Collapsed;

        public string EditLabel { get; } = Text("تعديل", "Edit");

        public string ArchiveLabel { get; } = Text("أرشفة", "Archive");

        public string RestoreLabel { get; } = Text("استعادة", "Restore");

        public string EditAutomationName { get; } = Text("تعديل بيانات العميل", "Edit customer details");

        public string ArchiveAutomationName { get; } = Text("أرشفة العميل", "Archive customer");

        public string RestoreAutomationName { get; } = Text("استعادة العميل", "Restore customer");

        internal static CustomerRow From(VersionedCustomer customer) => new(
            customer.Customer.Id,
            customer.Revision,
            customer.Customer.NameArabic,
            customer.Customer.NameEnglish,
            customer.Customer.VatNumber,
            customer.Customer.CommercialRegistration,
            customer.Customer.Address,
            customer.Customer.Phone,
            customer.Customer.Email,
            customer.Customer.IsArchived);
    }
}

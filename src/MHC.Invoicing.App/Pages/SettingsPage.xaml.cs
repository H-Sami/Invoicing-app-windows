using System.Globalization;
using System.Reflection;
using MHC.Invoicing.App.IO;
using MHC.Invoicing.App.Localization;
using MHC.Invoicing.Application.Maintenance;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Application.Settings;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Infrastructure.Backup;
using MHC.Invoicing.Infrastructure.Persistence;
using MHC.Invoicing.Infrastructure.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MHC.Invoicing.App.Pages;

public sealed partial class SettingsPage : Page
{
    private const int CurrentSchemaVersion = DatabaseInitializer.SchemaVersion;
    private readonly BackupService _backupService = new();
    private RestoreMaintenanceCoordinator? _restoreCoordinator;
    private AppComposition? _services;
    private int? _profileRevision;
    private byte[]? _logoBytes;
    private string? _logoMimeType;

    public SettingsPage()
    {
        InitializeComponent();
        FlowDirection = LocalizationState.Language == "ar-SA" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        App app = (App)Microsoft.UI.Xaml.Application.Current;
        LanguagePicker.SelectedIndex = app.Preferences.Language == "en-US" ? 1 : 0;
        ThemePicker.SelectedIndex = app.Preferences.Theme switch
        {
            UiTheme.Light => 1,
            UiTheme.Dark => 2,
            _ => 0,
        };
        _services = app.Services;
        if (_services is not null)
        {
            ApplicationMaintenanceGate.Shared.Configure(
                _ => Task.CompletedTask,
                cancellationToken => ReopenAndValidateAsync(_services.ConnectionString, cancellationToken));
            _restoreCoordinator = new RestoreMaintenanceCoordinator(
                ApplicationMaintenanceGate.Shared,
                new BackupRestoreExecutor(_backupService),
                new SqlitePoolMaintenance());
        }
    }

    private static string L(string key) => LocalizationState.GetString(key);

    private static string F(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, L(key), values);

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            SetStatus(L("DatabaseAccessFailure.Message"));
            return;
        }

        try
        {
            await using IAsyncDisposable work = await ApplicationMaintenanceGate.Shared.EnterWorkAsync();
            await using MhcDbContext context = CreateContext(_services.ConnectionString);
            VersionedCompanyProfile? profile = await new CompanyProfileRepository(context).GetAsync();
            _profileRevision = profile?.Revision;
            if (profile is not null)
            {
                CompanyNameArabic.Text = profile.Profile.NameArabic;
                CompanyNameEnglish.Text = profile.Profile.NameEnglish ?? string.Empty;
                CompanyVatNumber.Text = profile.Profile.VatNumber;
                CompanyCommercialRegistration.Text = profile.Profile.CommercialRegistration ?? string.Empty;
                CompanyBranch.Text = profile.Profile.Branch;
                CompanyAddress.Text = profile.Profile.Address;
                CompanyOperatorName.Text = profile.Profile.OperatorName;
                DefaultPaymentMethodPicker.SelectedIndex = (int)profile.Profile.DefaultPaymentMethod - 1;
                _logoBytes = profile.Profile.LogoBytes?.ToArray();
                _logoMimeType = profile.Profile.LogoMimeType;
                LogoStatus.Text = _logoBytes is null ? L("CompanyLogoNone.Message") : L("CompanyLogoSelected.Message");
            }
        }
        catch (Exception)
        {
            SetStatus(L("CompanyProfileLoadFailure.Message"));
        }
    }

    private async void SavePreferences_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            SetStatus(L("DatabaseAccessFailure.Message"));
            return;
        }

        string language = LanguagePicker.SelectedIndex == 1 ? "en-US" : "ar-SA";
        ElementTheme theme = ThemePicker.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        try
        {
            CompanyProfileSettings profile = new(
                CompanyNameArabic.Text,
                CompanyNameEnglish.Text,
                CompanyVatNumber.Text,
                CompanyCommercialRegistration.Text,
                CompanyBranch.Text,
                CompanyAddress.Text,
                CompanyOperatorName.Text,
                (PaymentMethod)(DefaultPaymentMethodPicker.SelectedIndex + 1),
                _logoBytes,
                _logoMimeType);
            VersionedCompanyProfile saved;
            await using (IAsyncDisposable work = await ApplicationMaintenanceGate.Shared.EnterWorkAsync())
            {
                await using MhcDbContext context = CreateContext(_services.ConnectionString);
                saved = await new CompanyProfileRepository(context)
                    .SaveAsync(profile, _profileRevision);
            }

            _profileRevision = saved.Revision;
            SetStatus(L("SettingsSaved.Message"));
            await ((App)Microsoft.UI.Xaml.Application.Current).ApplyPreferencesAsync(language, theme);
        }
        catch (PersistenceConcurrencyException)
        {
            SetStatus(L("CompanyProfileConflict.Message"));
        }
        catch (ArgumentException)
        {
            SetStatus(L("CompanyProfileValidation.Message"));
        }
        catch (Exception)
        {
            SetStatus(L("SettingsSaveFailure.Message"));
        }
    }

    private async void ChooseLogo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileOpenPicker picker = new()
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                ViewMode = PickerViewMode.Thumbnail,
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            InitializePicker(picker);
            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            _logoBytes = await BoundedFileReader.ReadAllBytesAsync(file.Path, 2_000_000);
            _logoMimeType = string.Equals(file.FileType, ".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : "image/jpeg";
            LogoStatus.Text = L("CompanyLogoSelected.Message");
        }
        catch (FileTooLargeException)
        {
            SetStatus(L("CompanyLogoTooLarge.Message"));
        }
        catch (Exception exception)
        {
            SetStatus(UserFacingError.Localize(exception));
        }
    }

    private void RemoveLogo_Click(object sender, RoutedEventArgs e)
    {
        _logoBytes = null;
        _logoMimeType = null;
        LogoStatus.Text = L("CompanyLogoNone.Message");
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            SetStatus(L("DatabaseAccessFailure.Message"));
            return;
        }

        FileSavePicker picker = new()
        {
            SuggestedFileName = $"MHC-Invoices-{DateTimeOffset.Now:yyyyMMdd-HHmm}",
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add(L("BackupFileType.DisplayName"), [".mhcbak"]);
        InitializePicker(picker);
        StorageFile? destination = await picker.PickSaveFileAsync();
        if (destination is null)
        {
            return;
        }

        await RunMaintenanceUiAsync(async () =>
        {
            await using IAsyncDisposable work = await ApplicationMaintenanceGate.Shared.EnterWorkAsync();
            await _backupService.CreateAsync(
                _services.Paths.DatabasePath,
                _services.Paths.InvoicesDirectory,
                destination.Path,
                CurrentSchemaVersion,
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "4.0.0");
            SetStatus(F("BackupCreated.Format", destination.Path));
        });
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null || _restoreCoordinator is null)
        {
            SetStatus(L("DatabaseAccessFailure.Message"));
            return;
        }

        FileOpenPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".mhcbak");
        picker.FileTypeFilter.Add(".zip");
        InitializePicker(picker);
        StorageFile? package = await picker.PickSingleFileAsync();
        if (package is null)
        {
            return;
        }

        ContentDialog confirmation = new()
        {
            Title = L("RestoreConfirmation.Title"),
            Content = L("RestoreConfirmation.Message"),
            PrimaryButtonText = L("RestoreConfirmation.PrimaryButtonText"),
            CloseButtonText = L("CommonCancel.Content"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            FlowDirection = FlowDirection,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunMaintenanceUiAsync(async () =>
        {
            try
            {
                await _restoreCoordinator.RestoreAsync(new RestoreMaintenanceRequest(
                    package.Path,
                    _services.Paths.DatabasePath,
                    _services.Paths.InvoicesDirectory,
                    CurrentSchemaVersion,
                    DestructiveRestoreConfirmed: true));
                SetStatus(L("RestoreSuccess.Message"));
            }
            catch (RestoreMaintenanceException exception)
            {
                System.Diagnostics.Debug.WriteLine(exception);
                string artifacts = exception.RetainedRecoveryArtifacts.Count == 0
                    ? L("RestoreNoArtifacts.Message")
                    : string.Join(Environment.NewLine, exception.RetainedRecoveryArtifacts);
                SetStatus(F("RestoreFailure.Format", Environment.NewLine, artifacts));
            }
        });
    }

    private async Task RunMaintenanceUiAsync(Func<Task> operation)
    {
        CreateBackupButton.IsEnabled = false;
        RestoreBackupButton.IsEnabled = false;
        MaintenanceProgress.IsActive = true;
        MaintenanceProgress.Visibility = Visibility.Visible;
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            SetStatus(UserFacingError.Localize(exception));
        }
        finally
        {
            CreateBackupButton.IsEnabled = true;
            RestoreBackupButton.IsEnabled = true;
            MaintenanceProgress.IsActive = false;
            MaintenanceProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void InitializePicker(object picker)
    {
        nint windowHandle = Microsoft.UI.Win32Interop.GetWindowFromWindowId(
            XamlRoot.ContentIslandEnvironment.AppWindowId);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
    }

    private void SetStatus(string value) => MaintenanceStatus.Text = value;

    private static MhcDbContext CreateContext(string connectionString)
    {
        DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new MhcDbContext(options);
    }

    private static async Task ReopenAndValidateAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        DbContextOptions<MhcDbContext> options = new DbContextOptionsBuilder<MhcDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using MhcDbContext context = new(options);
        await new DatabaseInitializer(context).InitializeAsync(cancellationToken);
    }

    private sealed class BackupRestoreExecutor(BackupService service) : IRestoreExecutor
    {
        public Task<IRestoreExecution> RestoreAsync(
            RestoreMaintenanceRequest request,
            CancellationToken cancellationToken = default) =>
            service.RestoreAsync(
                request.PackagePath,
                request.DatabasePath,
                request.DocumentsDirectory,
                request.CurrentSchemaVersion,
                request.DestructiveRestoreConfirmed,
                cancellationToken);
    }

    private sealed class SqlitePoolMaintenance : ISqlitePoolMaintenance
    {
        public void ClearAllPools() => SqliteConnection.ClearAllPools();
    }
}

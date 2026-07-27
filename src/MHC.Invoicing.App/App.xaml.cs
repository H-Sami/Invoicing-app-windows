using MHC.Invoicing.App.Diagnostics;
using MHC.Invoicing.App.Localization;
using MHC.Invoicing.Application.Maintenance;
using MHC.Invoicing.Application.Preview;
using MHC.Invoicing.Application.Runtime;
using MHC.Invoicing.Application.Settings;
using MHC.Invoicing.Infrastructure.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MHC.Invoicing.App;

public partial class App : Microsoft.UI.Xaml.Application
{
#if LOCAL_QA
    internal const string StartupFailureWindowTitle = "MHC Invoices V4 - LOCAL QA — Startup failure";
    internal const string InstanceMutexName = @"Local\MHC.Technology.MHC.Invoices.V4.LocalQA";
#else
    internal const string StartupFailureWindowTitle = "MHC Invoices — Startup failure";
    internal const string InstanceMutexName = @"Local\MHC.Technology.MHC.Invoices.V4";
#endif
    private Window? _window;
    private UiPreferenceStore? _preferenceStore;
    private SingleInstanceLease? _instanceLease;

    internal AppComposition? Services { get; private set; }

    internal MainWindow? MainWindow => _window as MainWindow;

    internal UiPreferences Preferences { get; private set; } = UiPreferences.Default;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ = LaunchTopLevelAsync(args);
    }

    private async Task LaunchTopLevelAsync(LaunchActivatedEventArgs args)
    {
        _ = args;
        MainWindow? pendingWindow = null;
        try
        {
            if (!SingleInstanceLease.TryAcquire(
                    InstanceMutexName,
                    out _instanceLease))
            {
                return;
            }

            AppDataPaths paths = AppDataPaths.CreateDefault();
            paths.EnsureDirectoriesExist();
            await CleanupLaunchCopiesAsync();
            _preferenceStore = new UiPreferenceStore(Path.Combine(paths.RootDirectory, "preferences.json"));
            UiPreferences preferences = await _preferenceStore.LoadAsync();
            Preferences = preferences;
            LocalizationState.SetLanguage(preferences.Language);
            pendingWindow = new MainWindow(ToElementTheme(preferences.Theme));
            Services = await AppComposition.CreateAsync(pendingWindow.DocumentService);
            SetCurrentWindow(pendingWindow);
            pendingWindow = null;
        }
        catch (Exception exception)
        {
            StartupFailureLog.TryWrite(exception);
            pendingWindow?.Close();
            Services = null;
            _instanceLease?.Dispose();
            _instanceLease = null;
            ShowStartupFailure(exception);
        }
    }

    private void ShowStartupFailure(Exception exception)
    {
        System.Diagnostics.Debug.WriteLine(exception);
        TextBlock message = new()
        {
            Text = "تعذر تشغيل البرنامج. أغلقه وحاول مرة أخرى.\n\n" +
                "MHC Invoices could not start. Close the application and try again.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(32),
        };
        Window failureWindow = new() { Title = StartupFailureWindowTitle, Content = message };
        failureWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, failureWindow))
            {
                _window = null;
            }
        };
        _window = failureWindow;
        failureWindow.Activate();
    }

    internal async Task ApplyPreferencesAsync(string language, ElementTheme theme)
    {
        UiPreferenceStore preferenceStore = _preferenceStore
            ?? throw new InvalidOperationException("Application preferences are not initialized.");
        await preferenceStore.SaveAsync(new UiPreferences(language, ToUiTheme(theme)));
        Preferences = new UiPreferences(language, ToUiTheme(theme));
        LocalizationState.SetLanguage(language);
        await using IAsyncDisposable work = await ApplicationMaintenanceGate.Shared.EnterWorkAsync();
        Window? previous = _window;
        MainWindow window = new(theme);
        Services = await AppComposition.CreateAsync(window.DocumentService);
        SetCurrentWindow(window);
        previous?.Close();
    }

    private void SetCurrentWindow(MainWindow window)
    {
        _window = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _instanceLease?.Dispose();
                _instanceLease = null;
                _ = CleanupLaunchCopiesAsync();
            }
        };
        window.Activate();
    }

    private static async Task CleanupLaunchCopiesAsync()
    {
        try
        {
            await CanonicalPdfLaunchStore.CreateDefault()
                .CleanupAsync(TimeSpan.FromDays(7), 32, CancellationToken.None);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ElementTheme ToElementTheme(UiTheme theme) => theme switch
    {
        UiTheme.Light => ElementTheme.Light,
        UiTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private static UiTheme ToUiTheme(ElementTheme theme) => theme switch
    {
        ElementTheme.Light => UiTheme.Light,
        ElementTheme.Dark => UiTheme.Dark,
        _ => UiTheme.System,
    };
}

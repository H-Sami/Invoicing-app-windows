using MHC.Invoicing.App.IO;
using MHC.Invoicing.Application.Documents;
using MHC.Invoicing.Application.Preview;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace MHC.Invoicing.App.Documents;

/// <summary>
/// Owns the hardened WebView2 boundary used to preview, print, and render invoice HTML.
/// Create and call this service on the UI thread that owns the WebView2 control.
/// </summary>
public sealed class WebView2InvoiceDocumentService :
    IInvoicePreviewService,
    IInvoicePrintService,
    IInvoicePdfRenderer,
    IDisposable
{
    private const long MaximumCanonicalPdfBytes = 64L * 1024 * 1024;

    private readonly WebView2 _webView;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly InternalDataNavigationGrant _internalNavigationGrant = new();
    private Task? _initialization;
    private bool _disposed;
    private int _activeOperations;
    private string? _blockedNavigationUri;
    private string? _blockedResourceUri;

    public WebView2InvoiceDocumentService(WebView2 webView)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
    }

    public static string GetDefaultUserDataFolder()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return InvoicePreviewStoragePaths.GetWebView2DataDirectory(localAppData);
    }

    public Task ShowAsync(string html, CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(async () =>
        {
            await RunExclusiveAsync(
                async () => await NavigateToHtmlAsync(html, cancellationToken).ConfigureAwait(true),
                cancellationToken).ConfigureAwait(true);
            return true;
        }, cancellationToken);

    public Task PrintAsync(string html, CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(async () =>
        {
            await RunExclusiveAsync(
                async () =>
                {
                    await NavigateToHtmlAsync(html, cancellationToken).ConfigureAwait(true);
                    cancellationToken.ThrowIfCancellationRequested();
                    _webView.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System);
                },
                cancellationToken).ConfigureAwait(true);
            return true;
        }, cancellationToken);

    public Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default) =>
        RunOnUiThreadAsync(async () =>
        {
            byte[]? result = null;
            await RunExclusiveAsync(
                async () =>
                {
                    await NavigateToHtmlAsync(html, cancellationToken).ConfigureAwait(true);
                    result = await RenderPdfCoreAsync(cancellationToken).ConfigureAwait(true);
                },
                cancellationToken).ConfigureAwait(true);

            return result!;
        }, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _internalNavigationGrant.Cancel();
        _lifetimeCancellation.Cancel();
        if (_activeOperations == 0 && _webView.CoreWebView2 is not null)
        {
            DetachSecurityHandlers(_webView.CoreWebView2);
        }
    }

    private async Task EnsureInitializedAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureUiThread();
        _initialization ??= InitializeCoreAsync();
        await _initialization.ConfigureAwait(true);
    }

    private async Task InitializeCoreAsync()
    {
        string userDataFolder = GetDefaultUserDataFolder();
        Directory.CreateDirectory(userDataFolder);
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateWithOptionsAsync(
            null,
            userDataFolder,
            new CoreWebView2EnvironmentOptions());
        await _webView.EnsureCoreWebView2Async(environment);

        CoreWebView2 core = _webView.CoreWebView2;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;

        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.WebResourceRequested += OnWebResourceRequested;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
    }

    private async Task NavigateToHtmlAsync(string html, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);
        using CancellationTokenSource navigationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        CancellationToken effectiveCancellation = navigationCancellation.Token;
        effectiveCancellation.ThrowIfCancellationRequested();
        await EnsureInitializedAsync().ConfigureAwait(true);

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            _internalNavigationGrant.Cancel();
            if (args.IsSuccess)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(
                    new InvalidOperationException(
                        $"Invoice preview navigation failed: {args.WebErrorStatus}. " +
                        $"Blocked navigation: {DescribeUri(_blockedNavigationUri)}; " +
                        $"blocked resource: {DescribeUri(_blockedResourceUri)}."));
            }
        }

        _webView.NavigationCompleted += NavigationCompleted;
        try
        {
            _blockedNavigationUri = null;
            _blockedResourceUri = null;
            _internalNavigationGrant.Arm();
            _webView.NavigateToString(html);
            await completion.Task.WaitAsync(effectiveCancellation).ConfigureAwait(true);
        }
        finally
        {
            _internalNavigationGrant.Cancel();
            _webView.NavigationCompleted -= NavigationCompleted;
        }
    }

    private async Task<byte[]> RenderPdfCoreAsync(CancellationToken cancellationToken)
    {
        using TemporaryPdfFile temporaryFile = TemporaryPdfFile.Create();
        CoreWebView2PrintSettings printSettings = _webView.CoreWebView2.Environment.CreatePrintSettings();
        printSettings.ShouldPrintBackgrounds = true;
        printSettings.ShouldPrintHeaderAndFooter = false;
        bool printed = await _webView.CoreWebView2.PrintToPdfAsync(temporaryFile.Path, printSettings);
        if (!printed)
        {
            throw new InvalidOperationException("WebView2 could not render the invoice PDF.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] pdfBytes = await BoundedFileReader.ReadAllBytesAsync(
            temporaryFile.Path,
            MaximumCanonicalPdfBytes,
            cancellationToken).ConfigureAwait(false);
        await ValidatePdfAsync(pdfBytes, cancellationToken).ConfigureAwait(false);
        return pdfBytes;
    }

    private static async Task ValidatePdfAsync(byte[] pdfBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using InMemoryRandomAccessStream stream = new();
        using (DataWriter writer = new(stream))
        {
            writer.WriteBytes(pdfBytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        PdfDocument document;
        try
        {
            document = await PdfDocument.LoadFromStreamAsync(stream);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidDataException("WebView2 produced an invalid PDF document.", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (document.PageCount == 0)
        {
            throw new InvalidDataException("WebView2 produced a PDF with no pages.");
        }
    }

    private async Task RunExclusiveAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureUiThread();
        using CancellationTokenSource operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        await _operationLock.WaitAsync(operationCancellation.Token).ConfigureAwait(true);
        _activeOperations++;
        try
        {
            operationCancellation.Token.ThrowIfCancellationRequested();
            await operation().ConfigureAwait(true);
        }
        finally
        {
            _activeOperations--;
            _operationLock.Release();
            if (_disposed && _activeOperations == 0 && _webView.CoreWebView2 is not null)
            {
                DetachSecurityHandlers(_webView.CoreWebView2);
            }
        }
    }

    private Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_webView.DispatcherQueue.HasThreadAccess)
        {
            return operation();
        }

        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        bool queued = _webView.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(await operation().ConfigureAwait(true));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                registration.Dispose();
            }
        });
        if (!queued)
        {
            registration.Dispose();
            completion.TrySetException(new InvalidOperationException("The invoice renderer UI thread is unavailable."));
        }

        return completion.Task;
    }

    private void EnsureUiThread()
    {
        if (!_webView.DispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("Invoice WebView2 operations must run on its owning UI thread.");
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        bool isInternalDataNavigation = _internalNavigationGrant.TryConsume(args.Uri);
        if (!isInternalDataNavigation && !InvoiceWebContentPolicy.IsAllowedDocumentNavigation(args.Uri))
        {
            _blockedNavigationUri = args.Uri;
            args.Cancel = true;
        }
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
    }

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.Handled = true;
    }

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (sender is CoreWebView2 core && !InvoiceWebContentPolicy.IsAllowedResource(args.Request.Uri))
        {
            _blockedResourceUri = args.Request.Uri;
            args.Response = core.Environment.CreateWebResourceResponse(
                null,
                403,
                "Blocked",
                "Content-Type: text/plain");
        }
    }

    private void DetachSecurityHandlers(CoreWebView2 core)
    {
        core.NavigationStarting -= OnNavigationStarting;
        core.NewWindowRequested -= OnNewWindowRequested;
        core.PermissionRequested -= OnPermissionRequested;
        core.DownloadStarting -= OnDownloadStarting;
        core.WebResourceRequested -= OnWebResourceRequested;
    }

    private static string DescribeUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return "none";
        }

        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int separator = uri.IndexOf(',');
            string mediaType = separator < 0 ? "data:" : uri[..separator];
            return $"{mediaType} ({uri.Length} characters)";
        }

        const int maximumLength = 256;
        return uri.Length <= maximumLength ? uri : $"{uri[..maximumLength]}…";
    }
}

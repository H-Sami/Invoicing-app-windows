using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace MHC.Invoicing.App.Documents;

internal static class CanonicalPdfPreviewDialog
{
    private const uint PreviewWidth = 1100;

    internal static async Task ShowAsync(
        XamlRoot xamlRoot,
        FlowDirection flowDirection,
        byte[] canonicalPdfBytes,
        string title,
        string closeButtonText,
        string accessibleName,
        string accessibleDocumentText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(canonicalPdfBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(closeButtonText);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessibleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessibleDocumentText);
        cancellationToken.ThrowIfCancellationRequested();

        using InMemoryRandomAccessStream pdfStream = new();
        using (DataWriter writer = new(pdfStream))
        {
            writer.WriteBytes(canonicalPdfBytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        pdfStream.Seek(0);
        PdfDocument document;
        try
        {
            document = await PdfDocument.LoadFromStreamAsync(pdfStream);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidDataException("The stored canonical PDF is invalid.", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (document.PageCount == 0)
        {
            throw new InvalidDataException("The stored canonical PDF contains no pages.");
        }

        StackPanel pages = new() { Spacing = 16 };
        AutomationProperties.SetName(pages, accessibleName);
        AutomationProperties.SetAutomationId(pages, "InvoicePreview.Pages");

        for (uint pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using PdfPage page = document.GetPage(pageIndex);
            double ratio = page.Size.Height / page.Size.Width;
            uint previewHeight = checked((uint)Math.Ceiling(PreviewWidth * ratio));
            using InMemoryRandomAccessStream renderedPage = new();
            await page.RenderToStreamAsync(
                renderedPage,
                new PdfPageRenderOptions
                {
                    DestinationWidth = PreviewWidth,
                    DestinationHeight = previewHeight,
                });

            renderedPage.Seek(0);
            BitmapImage bitmap = new();
            await bitmap.SetSourceAsync(renderedPage);
            Image image = new()
            {
                Source = bitmap,
                MaxWidth = PreviewWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            };
            AutomationProperties.SetName(image, $"{accessibleName} {pageIndex + 1}");
            AutomationProperties.SetAutomationId(image, $"InvoicePreview.Page.{pageIndex + 1}");
            pages.Children.Add(new Border
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                Child = image,
            });
        }

        ScrollViewer viewer = new()
        {
            Content = pages,
            MaxHeight = 720,
            MaxWidth = PreviewWidth + 32,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Enabled,
        };

        TextBox accessibleDocument = new()
        {
            Header = accessibleName,
            Text = accessibleDocumentText,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 220,
        };
        AutomationProperties.SetName(accessibleDocument, accessibleName);
        AutomationProperties.SetAutomationId(accessibleDocument, "InvoicePreview.AccessibleDocument");
        StackPanel content = new() { Spacing = 12 };
        content.Children.Add(accessibleDocument);
        content.Children.Add(viewer);

        ContentDialog dialog = new()
        {
            Title = title,
            Content = content,
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
            FlowDirection = flowDirection,
        };
        AutomationProperties.SetName(dialog, accessibleName);
        AutomationProperties.SetAutomationId(dialog, "InvoicePreview.Dialog");
        await dialog.ShowAsync();
    }
}

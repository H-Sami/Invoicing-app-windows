using MHC.Invoicing.Application.Preview;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;

namespace MHC.Invoicing.App.Preview;

public sealed class InvoicePdfExportService(Window owner) : IInvoiceExportService
{
    public async Task<bool> SavePdfAsync(
        byte[] pdfBytes,
        string publicNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        if (pdfBytes.Length == 0)
        {
            throw new ArgumentException("The PDF payload cannot be empty.", nameof(pdfBytes));
        }

        cancellationToken.ThrowIfCancellationRequested();
        FileSavePicker picker = new()
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(InvoiceExportFileName.Create(publicNumber)),
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("PDF", [".pdf"]);
        nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        CachedFileManager.DeferUpdates(file);
        await FileIO.WriteBytesAsync(file, pdfBytes);
        FileUpdateStatus status = await CachedFileManager.CompleteUpdatesAsync(file);
        if (status != FileUpdateStatus.Complete)
        {
            throw new IOException($"Windows could not complete the PDF export: {status}.");
        }

        return true;
    }
}

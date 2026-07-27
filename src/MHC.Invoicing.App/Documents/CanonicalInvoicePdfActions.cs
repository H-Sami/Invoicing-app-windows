using System.Diagnostics;
using System.Globalization;
using MHC.Invoicing.App.Localization;
using MHC.Invoicing.App.Workflows;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Application.Preview;
using MHC.Invoicing.Domain.Invoices;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MHC.Invoicing.App.Documents;

public sealed class CanonicalInvoicePdfActions(FrameworkElement owner) : ICanonicalInvoicePdfActions
{
    private static readonly CanonicalPdfLaunchStore LaunchStore = CanonicalPdfLaunchStore.CreateDefault();

    public async Task PreviewAsync(
        byte[] canonicalPdfBytes,
        string publicNumber,
        InvoiceDocumentType documentType,
        InvoiceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await CanonicalPdfPreviewDialog.ShowAsync(
            owner.XamlRoot,
            owner.FlowDirection,
            canonicalPdfBytes,
            F(documentType == InvoiceDocumentType.CreditNote
                ? "CreditNotePreviewDialog.TitleFormat"
                : "InvoicePreviewDialog.TitleFormat", publicNumber),
            L("CommonClose.Content"),
            F(documentType == InvoiceDocumentType.CreditNote
                ? "CreditNotePreviewDialog.AutomationNameFormat"
                : "InvoicePreviewDialog.AutomationNameFormat", publicNumber),
            InvoiceAccessibleTextFormatter.Format(snapshot),
            cancellationToken);
    }

    public async Task PrintAsync(
        byte[] canonicalPdfBytes,
        string publicNumber,
        InvoiceDocumentType documentType,
        CancellationToken cancellationToken = default)
    {
        StorageFile file = await WriteLaunchCopyAsync(canonicalPdfBytes, publicNumber, cancellationToken);
        Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true, Verb = "print" });
    }

    public async Task<bool> ExportAsync(
        byte[] canonicalPdfBytes,
        string publicNumber,
        InvoiceDocumentType documentType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileSavePicker picker = new()
        {
            SuggestedFileName = SafeFileName(publicNumber),
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add(L("PdfFileType.DisplayName"), [".pdf"]);
        nint windowHandle = Microsoft.UI.Win32Interop.GetWindowFromWindowId(
            owner.XamlRoot.ContentIslandEnvironment.AppWindowId);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        StorageFile? destination = await picker.PickSaveFileAsync();
        if (destination is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await FileIO.WriteBytesAsync(destination, canonicalPdfBytes);
        return true;
    }

    private static async Task<StorageFile> WriteLaunchCopyAsync(
        byte[] bytes,
        string publicNumber,
        CancellationToken cancellationToken)
    {
        string path = await LaunchStore.CreateAsync(bytes, publicNumber, cancellationToken);
        await LaunchStore.CleanupAsync(TimeSpan.FromDays(7), 32, CancellationToken.None);
        return await StorageFile.GetFileFromPathAsync(path);
    }

    private static string SafeFileName(string publicNumber)
    {
        string safe = string.Concat(publicNumber.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(safe) ? "invoice" : safe;
    }

    private static string L(string key) => LocalizationState.GetString(key);

    private static string F(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, L(key), values);
}

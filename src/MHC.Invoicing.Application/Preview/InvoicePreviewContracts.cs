namespace MHC.Invoicing.Application.Preview;

public interface IInvoicePreviewService
{
    Task ShowAsync(string html, CancellationToken cancellationToken = default);
}

public interface IInvoicePrintService
{
    Task PrintAsync(string html, CancellationToken cancellationToken = default);
}

public interface IInvoiceExportService
{
    Task<bool> SavePdfAsync(
        byte[] pdfBytes,
        string publicNumber,
        CancellationToken cancellationToken = default);
}

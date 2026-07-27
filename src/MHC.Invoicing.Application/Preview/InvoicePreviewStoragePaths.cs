namespace MHC.Invoicing.Application.Preview;

public static class InvoicePreviewStoragePaths
{
    public static string GetWebView2DataDirectory(string localAppDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(localAppDataDirectory))
        {
            throw new ArgumentException(
                "The local application-data directory is required.",
                nameof(localAppDataDirectory));
        }

        return Path.Combine(
            localAppDataDirectory,
            "MHC Technology",
#if LOCAL_QA
            "MHC Invoices V4 Local QA",
#else
            "MHC Invoices V4",
#endif
            "WebView2");
    }
}

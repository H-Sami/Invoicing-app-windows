namespace MHC.Invoicing.Infrastructure.Storage;

public sealed record AppDataPaths
{
    private AppDataPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        DataDirectory = Path.Combine(rootDirectory, "Data");
        DatabasePath = Path.Combine(DataDirectory, "mhc-invoices.db");
        InvoicesDirectory = Path.Combine(rootDirectory, "Invoices");
        BackupsDirectory = Path.Combine(rootDirectory, "Backups");
        WebView2Directory = Path.Combine(rootDirectory, "WebView2");
    }

    public string RootDirectory { get; }

    public string DataDirectory { get; }

    public string DatabasePath { get; }

    public string InvoicesDirectory { get; }

    public string BackupsDirectory { get; }

    public string WebView2Directory { get; }

    public static AppDataPaths CreateDefault() =>
        Create(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static AppDataPaths Create(string localApplicationData)
    {
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new ArgumentException("Local application-data directory is required.", nameof(localApplicationData));
        }

        string root = Path.Combine(
            Path.GetFullPath(localApplicationData),
            "MHC Technology",
#if LOCAL_QA
            "MHC Invoices V4 Local QA");
#else
            "MHC Invoices V4");
#endif
        return new AppDataPaths(root);
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(InvoicesDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(WebView2Directory);
    }
}

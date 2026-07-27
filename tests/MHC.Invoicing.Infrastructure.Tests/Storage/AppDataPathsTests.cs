using MHC.Invoicing.Infrastructure.Storage;

namespace MHC.Invoicing.Infrastructure.Tests.Storage;

public sealed class AppDataPathsTests
{
    [Fact]
    public void Create_UsesRequiredLocalApplicationDataHierarchy()
    {
        string localAppData = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}");

        AppDataPaths paths = AppDataPaths.Create(localAppData);

        Assert.Equal(
#if LOCAL_QA
            Path.Combine(localAppData, "MHC Technology", "MHC Invoices V4 Local QA"),
#else
            Path.Combine(localAppData, "MHC Technology", "MHC Invoices V4"),
#endif
            paths.RootDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Data"), paths.DataDirectory);
        Assert.Equal(Path.Combine(paths.DataDirectory, "mhc-invoices.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Invoices"), paths.InvoicesDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "Backups"), paths.BackupsDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "WebView2"), paths.WebView2Directory);
    }

    [Fact]
    public void EnsureDirectoriesExist_CreatesOnlyApplicationOwnedDirectories()
    {
        string localAppData = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}");
        try
        {
            AppDataPaths paths = AppDataPaths.Create(localAppData);

            paths.EnsureDirectoriesExist();

            Assert.True(Directory.Exists(paths.RootDirectory));
            Assert.True(Directory.Exists(paths.DataDirectory));
            Assert.True(Directory.Exists(paths.InvoicesDirectory));
            Assert.True(Directory.Exists(paths.BackupsDirectory));
            Assert.True(Directory.Exists(paths.WebView2Directory));
            Assert.False(File.Exists(paths.DatabasePath));
        }
        finally
        {
            if (Directory.Exists(localAppData))
            {
                Directory.Delete(localAppData, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Create_RejectsMissingLocalApplicationData(string value)
    {
        Assert.Throws<ArgumentException>(() => AppDataPaths.Create(value));
    }
}

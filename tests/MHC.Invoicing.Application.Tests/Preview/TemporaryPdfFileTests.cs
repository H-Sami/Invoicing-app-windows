using MHC.Invoicing.Application.Preview;

namespace MHC.Invoicing.Application.Tests.Preview;

public sealed class TemporaryPdfFileTests
{
    [Theory]
    [InlineData("MHC-2026-100", "MHC-2026-100.pdf")]
    [InlineData(" MHC-2026-101 ", "MHC-2026-101.pdf")]
    [InlineData("bad:/\\name", "bad___name.pdf")]
    public void ExportFilenameIsSafeAndPredictable(string publicNumber, string expected)
    {
        Assert.Equal(expected, InvoiceExportFileName.Create(publicNumber));
    }

    [Fact]
    public void WebView2DataDirectoryUsesApplicationOwnedRuntimeRoot()
    {
        string localAppData = Path.Combine("C:", "Users", "Test", "AppData", "Local");

        string path = InvoicePreviewStoragePaths.GetWebView2DataDirectory(localAppData);

        Assert.Equal(
#if LOCAL_QA
            Path.Combine(localAppData, "MHC Technology", "MHC Invoices V4 Local QA", "WebView2"),
#else
            Path.Combine(localAppData, "MHC Technology", "MHC Invoices V4", "WebView2"),
#endif
            path);
    }

    [Fact]
    public void CanonicalLaunchDirectoryUsesFlavorOwnedRuntimeRoot()
    {
        string localAppData = Path.Combine("C:", "Users", "Test", "AppData", "Local");

        string path = CanonicalPdfLaunchStore.GetDefaultRootDirectory(localAppData);

        Assert.Equal(
#if LOCAL_QA
            Path.Combine(localAppData, "MHC Technology", "MHC Invoices V4 Local QA", "Runtime", "CanonicalPdfLaunch"),
#else
            Path.Combine(localAppData, "MHC Technology", "MHC Invoices V4", "Runtime", "CanonicalPdfLaunch"),
#endif
            path);
    }

    [Fact]
    public void CreatesUniquePdfPathsUnderTheOperatingSystemTempDirectory()
    {
        using TemporaryPdfFile first = TemporaryPdfFile.Create();
        using TemporaryPdfFile second = TemporaryPdfFile.Create();
        string expectedDirectory = TemporaryPdfFile.GetDefaultDirectory(Path.GetTempPath());

        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(".pdf", Path.GetExtension(first.Path));
        Assert.Equal(
            Path.GetFullPath(expectedDirectory),
            Path.GetDirectoryName(Path.GetFullPath(first.Path)),
            ignoreCase: true);
#if LOCAL_QA
        Assert.Contains("MHC.Invoicing.LocalQA", first.Path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            $"{Path.DirectorySeparatorChar}MHC.Invoicing{Path.DirectorySeparatorChar}Pdf",
            first.Path,
            StringComparison.OrdinalIgnoreCase);
#else
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}MHC.Invoicing{Path.DirectorySeparatorChar}Pdf",
            first.Path,
            StringComparison.OrdinalIgnoreCase);
#endif
    }

    [Fact]
    public async Task DisposeDeletesGeneratedPdf()
    {
        TemporaryPdfFile temporaryFile = TemporaryPdfFile.Create();
        await File.WriteAllTextAsync(temporaryFile.Path, "pdf", TestContext.Current.CancellationToken);

        temporaryFile.Dispose();

        Assert.False(File.Exists(temporaryFile.Path));
    }

    [Fact]
    public async Task CanonicalLaunchStoreBoundsRetentionAndRemovesExpiredCopies()
    {
        string root = Path.Combine(Path.GetTempPath(), $"launch-store-tests-{Guid.NewGuid():N}");
        try
        {
            CanonicalPdfLaunchStore store = new(root);
            string expired = await store.CreateAsync([1], "MHC-2026-100", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-10));
            _ = await store.CreateAsync([2], "MHC-2026-101", TestContext.Current.CancellationToken);
            string newest = await store.CreateAsync([3], "MHC-2026-102", TestContext.Current.CancellationToken);

            await store.CleanupAsync(TimeSpan.FromDays(1), 1, TestContext.Current.CancellationToken);

            Assert.False(File.Exists(expired));
            Assert.Equal([newest], Directory.EnumerateFiles(root, "*.pdf").ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}

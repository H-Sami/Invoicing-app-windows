namespace MHC.Invoicing.Application.Preview;

/// <summary>
/// Provides a unique OS-temporary PDF path and best-effort deterministic cleanup.
/// </summary>
public sealed class TemporaryPdfFile : IDisposable
{
    private bool _disposed;

    private TemporaryPdfFile(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TemporaryPdfFile Create()
    {
        string directory = GetDefaultDirectory(System.IO.Path.GetTempPath());
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, $"{Guid.NewGuid():N}.pdf");
        return new TemporaryPdfFile(path);
    }

    public static string GetDefaultDirectory(string temporaryDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDirectory);
        return System.IO.Path.Combine(
            temporaryDirectory,
#if LOCAL_QA
            "MHC.Invoicing.LocalQA",
#else
            "MHC.Invoicing",
#endif
            "Pdf");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // Cleanup is best effort and must not mask the primary rendering outcome.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best effort and must not mask the primary rendering outcome.
        }
    }
}

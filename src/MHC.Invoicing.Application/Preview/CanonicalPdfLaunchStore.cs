using System.Globalization;

namespace MHC.Invoicing.Application.Preview;

public sealed class CanonicalPdfLaunchStore
{
    private static long _lastCreationToken;
    private readonly string _rootDirectory;

    public CanonicalPdfLaunchStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public static CanonicalPdfLaunchStore CreateDefault()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new CanonicalPdfLaunchStore(GetDefaultRootDirectory(localAppData));
    }

    public static string GetDefaultRootDirectory(string localAppDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataDirectory);
        return Path.Combine(
            localAppDataDirectory,
            "MHC Technology",
#if LOCAL_QA
            "MHC Invoices V4 Local QA",
#else
            "MHC Invoices V4",
#endif
            "Runtime",
            "CanonicalPdfLaunch");
    }

    public async Task<string> CreateAsync(
        byte[] canonicalPdfBytes,
        string publicNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalPdfBytes);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_rootDirectory);
        string fileName = InvoiceExportFileName.Create(publicNumber);
        long creationToken = NextCreationToken();
        string path = Path.Combine(
            _rootDirectory,
            $"{Path.GetFileNameWithoutExtension(fileName)}-{creationToken:D19}-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, canonicalPdfBytes, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public Task CleanupAsync(
        TimeSpan maximumAge,
        int maximumRetainedFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetainedFiles);
        if (!Directory.Exists(_rootDirectory))
        {
            return Task.CompletedTask;
        }

        DateTime cutoff = DateTime.UtcNow - maximumAge;
        FileInfo[] files = new DirectoryInfo(_rootDirectory)
            .EnumerateFiles("*.pdf", SearchOption.TopDirectoryOnly)
            .OrderByDescending(static file => file.LastWriteTimeUtc)
            .ThenByDescending(static file => CreationToken(file.Name))
            .ToArray();
        for (int index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index >= maximumRetainedFiles || files[index].LastWriteTimeUtc < cutoff)
            {
                TryDelete(files[index].FullName);
            }
        }
        return Task.CompletedTask;
    }

    private static long NextCreationToken()
    {
        while (true)
        {
            long observed = Volatile.Read(ref _lastCreationToken);
            long candidate = Math.Max(DateTime.UtcNow.Ticks, checked(observed + 1));
            if (Interlocked.CompareExchange(ref _lastCreationToken, candidate, observed) == observed)
            {
                return candidate;
            }
        }
    }

    private static long CreationToken(string fileName)
    {
        ReadOnlySpan<char> stem = Path.GetFileNameWithoutExtension(fileName.AsSpan());
        int lastSeparator = stem.LastIndexOf('-');
        int tokenSeparator = lastSeparator < 0 ? -1 : stem[..lastSeparator].LastIndexOf('-');
        return tokenSeparator >= 0 && lastSeparator > tokenSeparator + 1 &&
               long.TryParse(
                   stem[(tokenSeparator + 1)..lastSeparator],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out long token)
            ? token
            : 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

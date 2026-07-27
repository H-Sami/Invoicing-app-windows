namespace MHC.Invoicing.App.IO;

public sealed class FileTooLargeException(string message) : IOException(message);

public static class BoundedFileReader
{
    public static async Task<byte[]> ReadAllBytesAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes || stream.Length > int.MaxValue)
        {
            throw new FileTooLargeException("The selected file exceeds the allowed size.");
        }

        byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        byte[] extra = new byte[1];
        if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new FileTooLargeException("The selected file changed while it was being read.");
        }

        return bytes;
    }
}

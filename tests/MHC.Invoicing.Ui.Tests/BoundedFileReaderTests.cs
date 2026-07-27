using MHC.Invoicing.App.IO;

namespace MHC.Invoicing.Ui.Tests;

public sealed class BoundedFileReaderTests
{
    [Fact]
    public async Task ReadAllBytesAsync_RejectsFileLargerThanLimit()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.bin");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[17], TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<FileTooLargeException>(() =>
                BoundedFileReader.ReadAllBytesAsync(path, 16, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAllBytesAsync_ReturnsExactBytesAtLimit()
    {
        string path = Path.Combine(Path.GetTempPath(), $"hermes-verify-{Guid.NewGuid():N}.bin");
        byte[] expected = [1, 2, 3, 4];
        try
        {
            await File.WriteAllBytesAsync(path, expected, TestContext.Current.CancellationToken);

            byte[] actual = await BoundedFileReader.ReadAllBytesAsync(
                path, expected.Length, TestContext.Current.CancellationToken);

            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Domain.Tests.Numbering;

public sealed class DocumentSerialTests
{
    [Fact]
    public void Create_UsesAUuidVersionSeven()
    {
        DocumentSerial serial = DocumentSerial.Create();

        Assert.NotEqual(Guid.Empty, serial.Value);
        Assert.Equal(7, serial.Value.Version);
        Assert.Equal(serial.Value.ToString("D"), serial.ToString());
    }

    [Fact]
    public void Constructor_RejectsEmptyUuid()
    {
        Assert.Throws<ArgumentException>(() => new DocumentSerial(Guid.Empty));
    }
}

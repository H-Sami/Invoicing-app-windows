using System.Text;
using MHC.Invoicing.Domain.ValueObjects;
using MHC.Invoicing.Infrastructure.Documents;

namespace MHC.Invoicing.Infrastructure.Tests.Documents;

public sealed class ZatcaTlvEncoderTests
{
    [Fact]
    public void Encode_UsesUtf8ByteLengthsAndInvariantSaudiValues()
    {
        ZatcaQrData data = new(
            "مؤسسة إم إتش سي",
            "310123456700003",
            new DateTimeOffset(2026, 7, 23, 5, 6, 7, TimeSpan.FromHours(3)),
            Money.FromRiyals(115.25m),
            Money.FromRiyals(15.03m));

        byte[] encoded = ZatcaTlvEncoder.Encode(data);
        Dictionary<byte, string> decoded = Decode(encoded);

        Assert.Equal("مؤسسة إم إتش سي", decoded[1]);
        Assert.Equal("310123456700003", decoded[2]);
        Assert.Equal("2026-07-23T05:06:07+03:00", decoded[3]);
        Assert.Equal("115.25", decoded[4]);
        Assert.Equal("15.03", decoded[5]);
        Assert.Equal(Encoding.UTF8.GetByteCount("مؤسسة إم إتش سي"), encoded[1]);
    }

    [Fact]
    public void Encode_RejectsValuesLongerThanOneByteTlvLength()
    {
        ZatcaQrData data = new(
            new string('أ', 128),
            "310123456700003",
            new DateTimeOffset(2026, 7, 23, 5, 6, 7, TimeSpan.FromHours(3)),
            Money.FromRiyals(1m),
            Money.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(() => ZatcaTlvEncoder.Encode(data));
    }

    [Fact]
    public void GeneratePng_ProducesPngForExactBase64Payload()
    {
        ZatcaQrData data = new(
            "MHC Technology",
            "310123456700003",
            new DateTimeOffset(2026, 7, 23, 5, 6, 7, TimeSpan.FromHours(3)),
            Money.FromRiyals(115m),
            Money.FromRiyals(15m));
        ZatcaQrGenerator generator = new();

        ZatcaQrCode result = generator.Generate(data);

        Assert.Equal(Convert.ToBase64String(ZatcaTlvEncoder.Encode(data)), result.Base64Payload);
        Assert.True(result.PngBytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
    }

    private static Dictionary<byte, string> Decode(byte[] encoded)
    {
        Dictionary<byte, string> values = [];
        int offset = 0;
        while (offset < encoded.Length)
        {
            byte tag = encoded[offset++];
            int length = encoded[offset++];
            values.Add(tag, Encoding.UTF8.GetString(encoded, offset, length));
            offset += length;
        }

        Assert.Equal(encoded.Length, offset);
        return values;
    }
}

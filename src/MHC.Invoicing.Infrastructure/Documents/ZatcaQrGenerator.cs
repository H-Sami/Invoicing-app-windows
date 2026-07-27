using QRCoder;

namespace MHC.Invoicing.Infrastructure.Documents;

public sealed record ZatcaQrCode(string Base64Payload, byte[] PngBytes);

public interface IZatcaQrGenerator
{
    ZatcaQrCode Generate(ZatcaQrData data);
}

public sealed class ZatcaQrGenerator : IZatcaQrGenerator
{
    public ZatcaQrCode Generate(ZatcaQrData data)
    {
        string payload = Convert.ToBase64String(ZatcaTlvEncoder.Encode(data));
        using QRCodeData qrData = QRCodeGenerator.GenerateQrCode(
            payload,
            QRCodeGenerator.ECCLevel.Q,
            forceUtf8: true,
            utf8BOM: false);
        using PngByteQRCode qrCode = new(qrData);
        return new ZatcaQrCode(payload, qrCode.GetGraphic(pixelsPerModule: 8));
    }
}

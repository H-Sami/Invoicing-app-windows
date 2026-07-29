using System.Globalization;
using System.Text;
using MHC.Invoicing.Domain.Validation;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Infrastructure.Documents;

public sealed record ZatcaQrData(
    string SellerName,
    string SellerVatNumber,
    DateTimeOffset IssuedAtSaudi,
    Money GrandTotal,
    Money VatTotal);

public static class ZatcaTlvEncoder
{
    public static byte[] Encode(ZatcaQrData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(data.SellerName))
        {
            throw new ArgumentException("Seller name is required.", nameof(data));
        }

        if (string.IsNullOrWhiteSpace(data.SellerVatNumber) ||
            data.SellerVatNumber.Length > DomainFieldLimits.TaxIdentifier ||
            !data.SellerVatNumber.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                $"Seller VAT number must contain at most {DomainFieldLimits.TaxIdentifier} digits.",
                nameof(data));
        }

        if (data.IssuedAtSaudi.Offset != TimeSpan.FromHours(3))
        {
            throw new ArgumentException("ZATCA timestamp must use the Saudi +03:00 offset.", nameof(data));
        }

        if (data.GrandTotal <= Money.Zero || data.VatTotal < Money.Zero || data.VatTotal > data.GrandTotal)
        {
            throw new ArgumentException("ZATCA totals are invalid.", nameof(data));
        }

        using MemoryStream output = new();
        Write(output, 1, data.SellerName.Trim());
        Write(output, 2, data.SellerVatNumber);
        Write(output, 3, data.IssuedAtSaudi.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
        Write(output, 4, data.GrandTotal.Riyals.ToString("0.00", CultureInfo.InvariantCulture));
        Write(output, 5, data.VatTotal.Riyals.ToString("0.00", CultureInfo.InvariantCulture));
        return output.ToArray();
    }

    private static void Write(Stream output, byte tag, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "A ZATCA TLV value cannot exceed 255 UTF-8 bytes.");
        }

        output.WriteByte(tag);
        output.WriteByte((byte)bytes.Length);
        output.Write(bytes);
    }
}

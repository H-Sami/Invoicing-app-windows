namespace MHC.Invoicing.Application.Preview;

public static class InvoiceExportFileName
{
    public static string Create(string publicNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicNumber);
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string safeName = new(publicNumber.Trim()
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeName))
        {
            throw new ArgumentException("The invoice number does not produce a valid file name.", nameof(publicNumber));
        }

        return safeName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? safeName
            : $"{safeName}.pdf";
    }
}

using System.Globalization;
using System.Text;

namespace MHC.Invoicing.Domain.Search;

public static class ArabicSearchNormalizer
{
    private const char Tatweel = '\u0640';

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string decomposed = value.Normalize(NormalizationForm.FormD);
        StringBuilder output = new(decomposed.Length);
        bool previousWasSpace = true;

        foreach (char raw in decomposed)
        {
            if (raw == Tatweel || CharUnicodeInfo.GetUnicodeCategory(raw) is UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            char normalized = NormalizeCharacter(raw);
            if (char.IsWhiteSpace(normalized))
            {
                if (!previousWasSpace)
                {
                    output.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            output.Append(char.ToLowerInvariant(normalized));
            previousWasSpace = false;
        }

        return output.ToString().TrimEnd().Normalize(NormalizationForm.FormC);
    }

    private static char NormalizeCharacter(char value) => value switch
    {
        'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
        'ى' => 'ي',
        'ة' => 'ه',
        '٠' => '0',
        '١' => '1',
        '٢' => '2',
        '٣' => '3',
        '٤' => '4',
        '٥' => '5',
        '٦' => '6',
        '٧' => '7',
        '٨' => '8',
        '٩' => '9',
        _ => value,
    };
}

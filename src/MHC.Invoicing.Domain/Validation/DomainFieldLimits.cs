namespace MHC.Invoicing.Domain.Validation;

public static class DomainFieldLimits
{
    public const int PartyName = 200;
    public const int Address = 500;
    public const int Phone = 50;
    public const int Email = 254;
    public const int CommercialRegistration = 10;
    public const int Sku = 64;
    public const int Unit = 32;
    public const int LineDescription = 500;
    public const int Title = 200;
    public const int Notes = 2_000;
    public const int TaxExemptionReasonCode = 50;
}

internal static class DomainTextRules
{
    public static string Required(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        string normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", parameterName);
        }

        return normalized;
    }

    public static string? Optional(string? value, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", parameterName);
        }

        return normalized;
    }

    public static string? OptionalDigits(string? value, int exactLength, string parameterName)
    {
        string? normalized = Optional(value, exactLength, parameterName);
        if (normalized is not null &&
            (normalized.Length != exactLength || !normalized.All(char.IsAsciiDigit)))
        {
            throw new ArgumentException($"Value must contain exactly {exactLength} digits.", parameterName);
        }

        return normalized;
    }
}

namespace MHC.Invoicing.Domain.ValueObjects;

public readonly record struct Money(long Halalah) : IComparable<Money>, IFormattable
{
    public const string Currency = "SAR";

    public static readonly Money Zero = new(0);

    public decimal Riyals => Halalah / 100m;

    public static Money FromRiyals(decimal riyals) =>
        new(decimal.ToInt64(decimal.Round(
            riyals * 100m,
            0,
            MidpointRounding.AwayFromZero)));

    public Money Multiply(decimal multiplier) => FromRiyals(Riyals * multiplier);

    public int CompareTo(Money other) => Halalah.CompareTo(other.Halalah);

    public override string ToString() => ToString("N2", System.Globalization.CultureInfo.CurrentCulture);

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        $"{Riyals.ToString(format ?? "N2", formatProvider)} {Currency}";

    public static Money operator +(Money left, Money right) =>
        new(checked(left.Halalah + right.Halalah));

    public static Money operator -(Money left, Money right) =>
        new(checked(left.Halalah - right.Halalah));

    public static Money operator -(Money value) => new(checked(-value.Halalah));

    public static bool operator <(Money left, Money right) => left.Halalah < right.Halalah;

    public static bool operator >(Money left, Money right) => left.Halalah > right.Halalah;

    public static bool operator <=(Money left, Money right) => left.Halalah <= right.Halalah;

    public static bool operator >=(Money left, Money right) => left.Halalah >= right.Halalah;
}

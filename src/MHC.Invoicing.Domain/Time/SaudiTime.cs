namespace MHC.Invoicing.Domain.Time;

public static class SaudiTime
{
    private static readonly Lazy<TimeZoneInfo> Zone = new(ResolveZone);

    public static DateTimeOffset ToLocal(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant.ToUniversalTime(), Zone.Value);

    private static TimeZoneInfo ResolveZone()
    {
        string id = OperatingSystem.IsWindows() ? "Arab Standard Time" : "Asia/Riyadh";
        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }
}

public readonly record struct IssueTiming
{
    private IssueTiming(
        DateOnly businessDate,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset issuedAtSaudi)
    {
        BusinessDate = businessDate;
        IssuedAtUtc = issuedAtUtc;
        IssuedAtSaudi = issuedAtSaudi;
    }

    public DateOnly BusinessDate { get; }

    public DateTimeOffset IssuedAtUtc { get; }

    public DateTimeOffset IssuedAtSaudi { get; }

    public static IssueTiming Capture(DateOnly businessDate, DateTimeOffset actualInstant)
    {
        DateTimeOffset utc = actualInstant.ToUniversalTime();
        return new IssueTiming(businessDate, utc, SaudiTime.ToLocal(utc));
    }
}

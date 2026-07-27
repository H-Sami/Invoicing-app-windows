using MHC.Invoicing.App.Localization;

namespace MHC.Invoicing.Ui.Tests;

public sealed class DisplayCultureTests
{
    [Theory]
    [InlineData("ar-SA")]
    [InlineData("en-US")]
    public void Gregorian_UsesGregorianCalendarForBusinessDates(string language)
    {
        System.Globalization.CultureInfo culture = DisplayCulture.Gregorian(language);
        string formatted = new DateOnly(2026, 7, 23).ToString("d", culture);

        Assert.IsType<System.Globalization.GregorianCalendar>(culture.DateTimeFormat.Calendar);
        Assert.Contains("2026", formatted, StringComparison.Ordinal);
    }
}

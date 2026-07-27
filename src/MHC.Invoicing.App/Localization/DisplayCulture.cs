using System.Globalization;

namespace MHC.Invoicing.App.Localization;

public static class DisplayCulture
{
    public static CultureInfo Gregorian(string language)
    {
        CultureInfo culture = (CultureInfo)CultureInfo.GetCultureInfo(language).Clone();
        culture.DateTimeFormat.Calendar = new GregorianCalendar();
        return CultureInfo.ReadOnly(culture);
    }
}

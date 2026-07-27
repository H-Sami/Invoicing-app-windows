using System.Globalization;
using System.Text;
using MHC.Invoicing.App.Localization;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;

namespace MHC.Invoicing.App.Documents;

internal static class InvoiceAccessibleTextFormatter
{
    private const char LeftToRightIsolate = '\u2066';
    private const char PopDirectionalIsolate = '\u2069';

    internal static string Format(InvoiceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.DocumentType == InvoiceDocumentType.CreditNote &&
            string.IsNullOrWhiteSpace(snapshot.OriginalInvoicePublicNumber))
        {
            throw new ArgumentException(
                "Credit-note accessible text requires the original invoice public number.",
                nameof(snapshot));
        }

        bool english = LocalizationState.Language == "en-US";
        CultureInfo culture = DisplayCulture.Gregorian(LocalizationState.Language);
        StringBuilder text = new();

        text.AppendLine(snapshot.DocumentType == InvoiceDocumentType.CreditNote
            ? Localized(english, "Credit note", "إشعار دائن")
            : Localized(english, "Tax invoice", "فاتورة ضريبية"));
        AppendField(text, Localized(english, "Document number", "رقم المستند"), Isolate(snapshot.PublicNumber));
        AppendField(text, Localized(english, "Serial UUID", "المعرّف التسلسلي"), Isolate(snapshot.Id.ToString("D")));
        if (snapshot.DocumentType == InvoiceDocumentType.CreditNote)
        {
            AppendField(
                text,
                Localized(english, "Original invoice", "الفاتورة الأصلية"),
                Isolate(snapshot.OriginalInvoicePublicNumber ?? string.Empty));
        }

        AppendField(
            text,
            Localized(english, "Invoice date", "تاريخ الفاتورة"),
            Isolate(snapshot.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        AppendField(
            text,
            Localized(english, "Issued at", "وقت الإصدار"),
            Isolate(snapshot.IssuedAtSaudi.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)));
        AppendField(text, Localized(english, "Payment method", "طريقة الدفع"), PaymentText(snapshot.PaymentMethod, english));
        AppendField(text, Localized(english, "Operator", "المشغل"), snapshot.OperatorName);
        AppendField(text, Localized(english, "Branch", "الفرع"), snapshot.SellerBranch);

        AppendParty(text, Localized(english, "Seller", "البائع"), snapshot.Seller, english);
        if (snapshot.SellerLogoBytes is { Length: > 0 })
            text.AppendLine(Localized(english, "Seller logo", "شعار البائع"));
        text.AppendLine(Localized(english, "ZATCA QR code", "رمز الاستجابة السريعة لهيئة الزكاة والضريبة والجمارك"));
        AppendParty(text, Localized(english, "Customer", "العميل"), snapshot.Customer, english);

        AppendOptionalField(text, Localized(english, "Title", "العنوان"), snapshot.Title);
        text.AppendLine(Localized(english, "Items and services", "البنود والخدمات"));
        for (int index = 0; index < snapshot.Lines.Count; index++)
            AppendLine(text, snapshot.Lines[index], index + 1, english, culture);

        AppendMoney(text, Localized(english, "Subtotal", "المجموع قبل الضريبة"), snapshot.Subtotal.Riyals, english, culture);
        AppendMoney(text, Localized(english, "VAT", "ضريبة القيمة المضافة"), snapshot.Vat.Riyals, english, culture);
        AppendMoney(text, Localized(english, "Total including VAT", "الإجمالي شامل الضريبة"), snapshot.GrandTotal.Riyals, english, culture);
        if (snapshot.ShowNotes)
            AppendOptionalField(text, Localized(english, "Notes", "ملاحظات"), snapshot.Notes);
        text.AppendLine(Localized(
            english,
            "This document was generated locally.",
            "تم إنشاء هذا المستند محلياً."));
        return text.ToString().TrimEnd();
    }

    private static void AppendParty(StringBuilder text, string heading, PartySnapshot party, bool english)
    {
        text.AppendLine(heading);
        AppendField(text, Localized(english, "Arabic name", "الاسم بالعربية"), party.NameArabic);
        AppendOptionalField(text, Localized(english, "English name", "الاسم بالإنجليزية"), party.NameEnglish);
        AppendOptionalField(text, Localized(english, "VAT number", "الرقم الضريبي"), IsolateOptional(party.VatNumber));
        AppendOptionalField(
            text,
            Localized(english, "Commercial registration", "السجل التجاري"),
            IsolateOptional(party.CommercialRegistration));
        AppendOptionalField(text, Localized(english, "Address", "العنوان"), party.Address);
    }

    private static void AppendLine(
        StringBuilder text,
        InvoiceLineSnapshot line,
        int number,
        bool english,
        CultureInfo culture)
    {
        text.Append(Localized(english, "Line", "البند")).Append(' ').AppendLine(number.ToString(culture));
        AppendField(text, Localized(english, "Description", "البيان"), line.Description);
        AppendOptionalField(text, "SKU", IsolateOptional(line.Sku));
        AppendField(text, Localized(english, "Quantity", "الكمية"), Isolate(line.Quantity.ToString("N3", culture)));
        AppendField(text, Localized(english, "Unit", "الوحدة"), line.Unit);
        AppendMoney(text, Localized(english, "Unit price", "سعر الوحدة"), line.UnitPrice.Riyals, english, culture);
        decimal discount = Math.Max(0m, (line.UnitPrice.Riyals * line.Quantity) - line.Net.Riyals);
        AppendMoney(text, Localized(english, "Discount", "الخصم"), discount, english, culture);
        AppendMoney(text, Localized(english, "Net", "الصافي"), line.Net.Riyals, english, culture);
        AppendField(text, Localized(english, "VAT category", "فئة الضريبة"), VatCategoryText(line.VatCategory, english));
        AppendOptionalField(
            text,
            Localized(english, "Exemption code", "رمز الإعفاء"),
            IsolateOptional(line.TaxExemptionReasonCode));
        AppendOptionalField(
            text,
            Localized(english, "Exemption reason", "سبب الإعفاء"),
            line.TaxExemptionReason);
        AppendMoney(text, Localized(english, "VAT", "الضريبة"), line.Vat.Riyals, english, culture);
        AppendMoney(text, Localized(english, "Gross", "الإجمالي"), line.Gross.Riyals, english, culture);
    }

    private static string PaymentText(PaymentMethod paymentMethod, bool english) => paymentMethod switch
    {
        PaymentMethod.Cash => Localized(english, "Cash", "نقداً"),
        PaymentMethod.Card => Localized(english, "Card", "بطاقة"),
        PaymentMethod.BankTransfer => Localized(english, "Bank transfer", "تحويل بنكي"),
        PaymentMethod.Credit => Localized(english, "Credit", "آجل"),
        PaymentMethod.Other => Localized(english, "Other", "أخرى"),
        _ => throw new ArgumentOutOfRangeException(nameof(paymentMethod)),
    };

    private static string VatCategoryText(VatCategory category, bool english) => category switch
    {
        VatCategory.Standard15 => Localized(english, "Standard VAT 15%", "ضريبة قياسية 15٪"),
        VatCategory.ZeroRated => Localized(english, "Zero-rated VAT", "ضريبة بنسبة صفر"),
        VatCategory.Exempt => Localized(english, "Exempt", "معفى من الضريبة"),
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    private static void AppendMoney(
        StringBuilder text,
        string label,
        decimal riyals,
        bool english,
        CultureInfo culture) =>
        AppendField(
            text,
            label,
            $"{Isolate(riyals.ToString("N2", culture))} {Localized(english, "Saudi riyals", "ريال سعودي")}");

    private static void AppendField(StringBuilder text, string label, string value) =>
        text.Append(label).Append(": ").AppendLine(value);

    private static void AppendOptionalField(StringBuilder text, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            AppendField(text, label, value);
    }

    private static string? IsolateOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Isolate(value);

    private static string Isolate(string value) =>
        string.Concat(LeftToRightIsolate, value, PopDirectionalIsolate);

    private static string Localized(bool english, string englishText, string arabicText) =>
        english ? englishText : arabicText;
}

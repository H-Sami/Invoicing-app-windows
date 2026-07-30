using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using MHC.Invoicing.Application.Documents;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Infrastructure.Documents;

public sealed class InvoiceHtmlRenderer : IInvoiceHtmlRenderer
{
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Default;

    public string Render(InvoiceDocumentModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Validate(model);
        StringBuilder html = new(16_384);
        html.Append("""
            <!doctype html>
            <html lang="ar-SA" dir="rtl">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'">
            <style>
            @page { size: A4; margin: 12mm; }
            * { box-sizing: border-box; }
            body { margin: 0; color: #161616; background: #fff; font-family: "Segoe UI", Tahoma, Arial, sans-serif; font-size: 11px; line-height: 1.55; }
            .document { width: 100%; }
            .header { display: grid; grid-template-columns: 1fr auto; gap: 18px; align-items: start; border-bottom: 2px solid #222; padding-bottom: 12px; }
            .brand { display: flex; gap: 12px; align-items: center; }
            .logo { width: 72px; max-height: 72px; object-fit: contain; }
            .qr { width: 104px; height: 104px; image-rendering: crisp-edges; }
            h1 { margin: 0 0 4px; font-size: 22px; }
            .en { direction: ltr; unicode-bidi: isolate; color: #555; font-size: .9em; }
            .muted { color: #555; }
            .meta, .parties { display: grid; grid-template-columns: 1fr 1fr; gap: 10px 18px; margin-top: 14px; }
            .panel { border: 1px solid #777; border-radius: 5px; padding: 10px; break-inside: avoid; }
            .row { display: grid; grid-template-columns: 145px 1fr; gap: 8px; padding: 2px 0; }
            .label { color: #444; font-weight: 600; }
            table { width: 100%; border-collapse: collapse; margin-top: 14px; }
            thead { display: table-header-group; }
            th, td { border: 1px solid #666; padding: 6px; text-align: right; vertical-align: top; }
            th { background: #eee; font-weight: 700; }
            .number { direction: ltr; unicode-bidi: isolate; text-align: left; white-space: nowrap; }
            .totals { width: 46%; margin: 12px 0 0 auto; border: 1px solid #555; break-inside: avoid; }
            .totals .row { grid-template-columns: 1fr auto; padding: 6px 8px; border-bottom: 1px solid #aaa; }
            .totals .row:last-child { border-bottom: 0; font-size: 14px; font-weight: 700; }
            .notes { margin-top: 12px; white-space: pre-wrap; }
            .credit { border: 2px solid #333; padding: 7px 10px; margin-top: 10px; font-weight: 700; text-align: center; }
            tr { break-inside: avoid; }
            </style>
            </head><body><main class="document">
            """);

        html.Append("<header class=\"header\"><div class=\"brand\">");
        AppendImage(html, model.SellerLogoBytes, model.SellerLogoMimeType, "logo", "شعار البائع");
        html.Append("<div><h1>");
        Append(html, model.Seller.NameArabic);
        html.Append("</h1>");
        if (!string.IsNullOrWhiteSpace(model.Seller.NameEnglish))
        {
            html.Append("<div class=\"en\">");
            Append(html, model.Seller.NameEnglish);
            html.Append("</div>");
        }

        html.Append("<div class=\"muted\">");
        Append(html, model.Branch);
        html.Append("</div></div></div>");
        AppendImage(html, model.QrPngBytes, "image/png", "qr", "رمز الاستجابة السريعة");
        html.Append("</header>");

        string ArabicType = model.DocumentType == InvoiceDocumentType.CreditNote
            ? "إشعار دائن"
            : "فاتورة ضريبية";
        string EnglishType = model.DocumentType == InvoiceDocumentType.CreditNote
            ? "Credit Note"
            : "Tax Invoice";
        html.Append("<h1 style=\"margin-top:12px\">");
        Append(html, ArabicType);
        html.Append(" <span class=\"en\">");
        Append(html, EnglishType);
        html.Append("</span></h1>");
        if (model.DocumentType == InvoiceDocumentType.CreditNote)
        {
            html.Append("<div class=\"credit\">إشعار دائن / Credit Note — الفاتورة الأصلية / Original invoice: ");
            Append(html, model.OriginalPublicNumber);
            html.Append("</div>");
        }

        html.Append("<section class=\"meta panel\">");
        AppendField(html, "رقم المستند / Document No.", model.PublicNumber, number: true);
        AppendField(html, "المعرّف التسلسلي / Serial UUID", model.Serial.ToString("D"), number: true);
        AppendField(html, "تاريخ الفاتورة / Invoice date", model.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), number: true);
        AppendField(html, "وقت الإصدار / Issued at", model.IssuedAtSaudi.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), number: true);
        AppendField(html, "طريقة الدفع / Payment", model.PaymentMethod);
        AppendField(html, "المشغل / Operator", model.OperatorName);
        html.Append("</section>");

        html.Append("<section class=\"parties\">");
        AppendParty(html, "البائع / Seller", model.Seller);
        AppendParty(html, "العميل / Customer", model.Customer);
        html.Append("</section>");

        if (!string.IsNullOrWhiteSpace(model.Title))
        {
            html.Append("<h2>");
            Append(html, model.Title);
            html.Append("</h2>");
        }

        html.Append("<table><thead><tr><th>#</th><th>البيان / Description</th><th>الكمية / Qty</th><th>الوحدة / Unit</th><th>السعر / Price</th><th>الخصم / Discount</th><th>الصافي / Net</th><th>الضريبة / VAT</th><th>الإجمالي / Total</th></tr></thead><tbody>");
        for (int index = 0; index < model.Lines.Count; index++)
        {
            InvoiceDocumentLine line = model.Lines[index];
            html.Append("<tr class=\"line-row\"><td class=\"number\">").Append(index + 1).Append("</td><td>");
            Append(html, line.Description);
            if (!string.IsNullOrWhiteSpace(line.Sku))
            {
                html.Append("<div class=\"muted en\">SKU: ");
                Append(html, line.Sku);
                html.Append("</div>");
            }

            html.Append("<div class=\"muted\">تصنيف الضريبة / VAT category: ");
            Append(html, VatCategoryLabel(line.VatCategory));
            html.Append("</div>");
            if (!string.IsNullOrWhiteSpace(line.TaxExemptionReasonCode))
            {
                html.Append("<div class=\"muted en\">Exemption code: ");
                Append(html, line.TaxExemptionReasonCode);
                html.Append("</div>");
            }
            if (!string.IsNullOrWhiteSpace(line.TaxExemptionReason))
            {
                html.Append("<div class=\"muted\">سبب الإعفاء / Exemption reason: ");
                Append(html, line.TaxExemptionReason);
                html.Append("</div>");
            }

            html.Append("</td><td class=\"number\">");
            Append(html, line.Quantity.ToString("0.###", CultureInfo.InvariantCulture));
            html.Append("</td><td>");
            Append(html, line.Unit);
            html.Append("</td>");
            AppendMoneyCell(html, line.UnitPrice);
            AppendMoneyCell(html, line.Discount);
            AppendMoneyCell(html, line.Net);
            AppendMoneyCell(html, line.Vat);
            AppendMoneyCell(html, line.Gross);
            html.Append("</tr>");
        }

        html.Append("</tbody></table><section class=\"totals\">");
        AppendTotal(html, "المجموع قبل الضريبة / Subtotal", model.Subtotal);
        AppendTotal(html, "ضريبة القيمة المضافة / VAT", model.Vat);
        AppendTotal(html, "الإجمالي / Grand total", model.GrandTotal);
        html.Append("</section>");
        if (model.ShowNotes && !string.IsNullOrWhiteSpace(model.Notes))
        {
            html.Append("<section class=\"notes panel\"><strong>ملاحظات / Notes</strong><div>");
            Append(html, model.Notes);
            html.Append("</div></section>");
        }

        html.Append("</main></body></html>");
        return html.ToString();
    }

    private static void Validate(InvoiceDocumentModel model)
    {
        if (string.IsNullOrWhiteSpace(model.PublicNumber) || model.Serial == Guid.Empty || model.Lines.Count == 0)
        {
            throw new ArgumentException("Invoice document identity and lines are required.", nameof(model));
        }

        if (model.DocumentType == InvoiceDocumentType.CreditNote && string.IsNullOrWhiteSpace(model.OriginalPublicNumber))
        {
            throw new ArgumentException("Credit documents require the original public number.", nameof(model));
        }

        if (model.Subtotal + model.Vat != model.GrandTotal)
        {
            throw new ArgumentException("Invoice document totals do not reconcile.", nameof(model));
        }
    }

    private static string VatCategoryLabel(VatCategory category) => category switch
    {
        VatCategory.Standard15 => "قياسي 15% / Standard 15%",
        VatCategory.ZeroRated => "نسبة صفر / Zero-rated",
        VatCategory.Exempt => "معفى / Exempt",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unsupported VAT category."),
    };

    private static void AppendParty(StringBuilder html, string heading, PartySnapshot party)
    {
        html.Append("<article class=\"panel\"><strong>");
        Append(html, heading);
        html.Append("</strong>");
        AppendField(html, "الاسم / Name", party.NameArabic);
        if (!string.IsNullOrWhiteSpace(party.NameEnglish))
        {
            AppendField(html, "الاسم بالإنجليزية / English name", party.NameEnglish);
        }

        AppendField(html, "الرقم الضريبي / VAT", party.VatNumber);
        AppendField(html, "السجل التجاري / CR", party.CommercialRegistration);
        AppendField(html, "العنوان / Address", party.Address);
        html.Append("</article>");
    }

    private static void AppendField(StringBuilder html, string label, string? value, bool number = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        html.Append("<div class=\"row\"><span class=\"label\">");
        Append(html, label);
        html.Append("</span><span");
        if (number)
        {
            html.Append(" class=\"number\"");
        }

        html.Append('>');
        Append(html, value);
        html.Append("</span></div>");
    }

    private static void AppendMoneyCell(StringBuilder html, Money money)
    {
        html.Append("<td class=\"number\">");
        Append(html, FormatMoney(money));
        html.Append("</td>");
    }

    private static void AppendTotal(StringBuilder html, string label, Money money)
    {
        html.Append("<div class=\"row\"><span>");
        Append(html, label);
        html.Append("</span><span class=\"number\">");
        Append(html, FormatMoney(money));
        html.Append("</span></div>");
    }

    private static string FormatMoney(Money money) =>
        $"{money.Riyals.ToString("0.00", CultureInfo.InvariantCulture)} SAR";

    private static void AppendImage(
        StringBuilder html,
        byte[]? bytes,
        string? mimeType,
        string cssClass,
        string alternateText)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return;
        }

        string safeMime = mimeType switch
        {
            "image/png" => "image/png",
            "image/jpeg" => "image/jpeg",
            _ => throw new ArgumentException("Only PNG and JPEG embedded images are supported.", nameof(mimeType)),
        };
        html.Append("<img class=\"").Append(cssClass).Append("\" src=\"data:")
            .Append(safeMime).Append(";base64,").Append(Convert.ToBase64String(bytes)).Append("\" alt=\"");
        Append(html, alternateText);
        html.Append("\">");
    }

    private static void Append(StringBuilder html, string? value) =>
        html.Append(Encoder.Encode(value ?? string.Empty));
}

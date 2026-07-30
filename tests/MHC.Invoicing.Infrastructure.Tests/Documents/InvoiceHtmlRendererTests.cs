using System.Text.Encodings.Web;
using MHC.Invoicing.Application.Documents;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;
using MHC.Invoicing.Infrastructure.Documents;

namespace MHC.Invoicing.Infrastructure.Tests.Documents;

public sealed class InvoiceHtmlRendererTests
{
    [Fact]
    public void Render_EncodesEveryDynamicValueAndUsesOfflineRtlA4Markup()
    {
        InvoiceDocumentModel model = CreateModel(
            customerName: "<script>alert('x')</script> عميل",
            notes: "A&B <img src=https://evil.example/x>",
            showNotes: true);
        InvoiceHtmlRenderer renderer = new();

        string html = renderer.Render(model);

        Assert.Contains("dir=\"rtl\"", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.Contains("@page", html, StringComparison.Ordinal);
        Assert.Contains("A4", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(&#x27;x&#x27;)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("A&amp;B &lt;img src=https://evil.example/x&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data:image/png;base64,AQIDBA==", html, StringComparison.Ordinal);
        Assert.Contains("MHC-2026-100", html, StringComparison.Ordinal);
        Assert.Contains(model.Serial.ToString("D"), html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", html, StringComparison.Ordinal);
        Assert.DoesNotContain("This document was generated locally", html, StringComparison.Ordinal);
        Assert.DoesNotContain("تم إنشاء هذا المستند محلياً", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OmitsDisabledNotesAndIdentifiesCreditOriginal()
    {
        InvoiceDocumentModel model = CreateModel("عميل", "secret notes", showNotes: false) with
        {
            DocumentType = InvoiceDocumentType.CreditNote,
            OriginalPublicNumber = "MHC-2026-100",
            PublicNumber = "MHC-2026-101",
        };
        InvoiceHtmlRenderer renderer = new();

        string html = renderer.Render(model);

        Assert.DoesNotContain("secret notes", html, StringComparison.Ordinal);
        Assert.Contains("إشعار دائن", html, StringComparison.Ordinal);
        Assert.Contains("Credit Note", html, StringComparison.Ordinal);
        Assert.Contains("MHC-2026-100", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ExposesVatCategoryExemptionEvidenceAndDiscount()
    {
        InvoiceDocumentModel seed = CreateModel("عميل", null, showNotes: false);
        InvoiceDocumentModel model = seed with
        {
            Lines =
            [
                seed.Lines[0] with
                {
                    VatCategory = VatCategory.Exempt,
                    TaxExemptionReasonCode = "VATEX-SA-29",
                    TaxExemptionReason = "Medical & <eligible>",
                    Discount = Money.FromRiyals(5m),
                },
            ],
        };

        string html = new InvoiceHtmlRenderer().Render(model);

        Assert.Contains(HtmlEncoder.Default.Encode("معفى / Exempt"), html, StringComparison.Ordinal);
        Assert.Contains("VATEX-SA-29", html, StringComparison.Ordinal);
        Assert.Contains("Medical &amp; &lt;eligible&gt;", html, StringComparison.Ordinal);
        Assert.Contains("الخصم / Discount", html, StringComparison.Ordinal);
        Assert.Contains(">5.00 SAR</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_HandlesOneHundredLinesWithRepeatingTableHeader()
    {
        InvoiceDocumentModel seed = CreateModel("عميل", null, showNotes: false);
        InvoiceDocumentModel model = seed with
        {
            Lines = Enumerable.Range(1, 100)
                .Select(index => seed.Lines[0] with
                {
                    Id = Guid.CreateVersion7(),
                    Description = $"خدمة {index}",
                })
                .ToArray(),
        };
        InvoiceHtmlRenderer renderer = new();

        string html = renderer.Render(model);

        Assert.Equal(100, CountOccurrences(html, "class=\"line-row\""));
        Assert.Contains("thead { display: table-header-group; }", html, StringComparison.Ordinal);
    }

    private static InvoiceDocumentModel CreateModel(string customerName, string? notes, bool showNotes) => new(
        "MHC-2026-100",
        Guid.CreateVersion7(),
        InvoiceDocumentType.TaxInvoice,
        null,
        new DateOnly(2026, 7, 23),
        new DateTimeOffset(2026, 7, 23, 5, 6, 7, TimeSpan.FromHours(3)),
        PartySnapshot.Create(
            "مؤسسة إم إتش سي",
            "MHC Technology",
            "310123456700003",
            "1234567890",
            "الرياض"),
        PartySnapshot.Create(customerName, "Customer", null, null, "جدة"),
        "الفرع الرئيسي",
        "المشغل",
        "Cash",
        "فاتورة ضريبية",
        notes,
        showNotes,
        [1, 2, 3],
        "image/png",
        [1, 2, 3, 4],
        [new InvoiceDocumentLine(
            Guid.CreateVersion7(),
            "خدمة استشارية",
            "CONSULT",
            "hour",
            2m,
            Money.FromRiyals(100m),
            VatCategory.Standard15,
            null,
            null,
            Money.Zero,
            Money.FromRiyals(200m),
            Money.FromRiyals(30m),
            Money.FromRiyals(230m))],
        Money.FromRiyals(200m),
        Money.FromRiyals(30m),
        Money.FromRiyals(230m));

    private static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }
}

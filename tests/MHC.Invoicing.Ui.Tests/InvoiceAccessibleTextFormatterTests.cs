using System.Reflection;
using MHC.Invoicing.App.Documents;
using MHC.Invoicing.App.Localization;
using MHC.Invoicing.Application.Persistence;
using MHC.Invoicing.Domain.Invoices;
using MHC.Invoicing.Domain.ValueObjects;

namespace MHC.Invoicing.Ui.Tests;

public sealed class InvoiceAccessibleTextFormatterTests
{
    [Fact]
    public void Format_EnglishCreditNote_ContainsCompleteCanonicalSemantics()
    {
        SetLanguageForTest("en-US");
        Guid id = Guid.Parse("019824f5-1ac0-7000-8000-000000000001");
        InvoiceSnapshot snapshot = CreateCompleteSnapshot(id);

        string text = Format(snapshot);

        string[] requiredText =
        [
            "Credit note", "Document number", "MHC-2026-100", "Serial UUID", id.ToString("D"),
            "Original invoice", "MHC-2026-099", "Invoice date", "2026-07-20", "Issued at",
            "2026-07-20 15:30:45 +03:00", "Payment method", "Bank transfer", "Operator", "Issuer Name",
            "Branch", "Main branch", "Seller", "شركة البائع", "Seller Company", "310123456789003",
            "1010123456", "Riyadh", "Customer", "شركة العميل", "Customer Company", "310987654321003",
            "2050123456", "Jeddah", "Title", "Consulting credit", "Line 1", "Description", "Consulting",
            "SKU", "SKU-1", "Quantity", "2.000", "Unit", "hour", "Unit price", "Discount", "100.00",
            "Net", "900.00", "VAT category", "Standard VAT 15%", "VAT", "135.00", "Gross", "1,035.00",
            "Line 2", "Exempt", "Exemption code", "VATEX-SA-29", "Exemption reason", "Qualifying exemption",
            "Subtotal", "Total including VAT", "Notes", "Customer adjustment",
        ];
        foreach (string expected in requiredText)
            Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.DoesNotContain("This document was generated locally", text, StringComparison.Ordinal);
        Assert.DoesNotContain("تم إنشاء هذا المستند محلياً", text, StringComparison.Ordinal);
        Assert.Contains($"\u2066{id:D}\u2069", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_CreditNoteWithoutOriginalPublicNumber_RejectsIncompleteLineage()
    {
        SetLanguageForTest("en-US");
        InvoiceSnapshot snapshot = CreateCompleteSnapshot(Guid.NewGuid()) with
        {
            OriginalInvoicePublicNumber = null,
        };

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() => Format(snapshot));

        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    [Fact]
    public void Format_Arabic_UsesLocalizedPaymentAndTaxTermsWithoutRawEnumNames()
    {
        SetLanguageForTest("ar-SA");

        string text = Format(CreateCompleteSnapshot(Guid.NewGuid()));

        Assert.Contains("إشعار دائن", text, StringComparison.Ordinal);
        Assert.Contains("طريقة الدفع: تحويل بنكي", text, StringComparison.Ordinal);
        Assert.Contains("فئة الضريبة: ضريبة قياسية 15٪", text, StringComparison.Ordinal);
        Assert.Contains("شركة البائع", text, StringComparison.Ordinal);
        Assert.Contains("Seller Company", text, StringComparison.Ordinal);
        Assert.Contains("ريال سعودي", text, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(PaymentMethod.BankTransfer), text, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(VatCategory.Standard15), text, StringComparison.Ordinal);
    }

    private static InvoiceSnapshot CreateCompleteSnapshot(Guid id) => new(
            id, 2026, 100, "MHC-2026-100", InvoiceDocumentType.CreditNote,
            Guid.Parse("019824f5-1ac0-7000-8000-000000000000"), "MHC-2026-099", Guid.NewGuid(),
            new DateOnly(2026, 7, 20),
            new DateTimeOffset(2026, 7, 20, 12, 30, 45, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 20, 15, 30, 45, TimeSpan.FromHours(3)),
            new PartySnapshot("شركة البائع", "Seller Company", "310123456789003", "1010123456", "Riyadh"),
            "Main branch", [1], "image/png", "Issuer Name",
            new PartySnapshot("شركة العميل", "Customer Company", "310987654321003", "2050123456", "Jeddah"),
            PaymentMethod.BankTransfer, "Consulting credit", "Customer adjustment", true, "SAR",
            new Money(90_000), new Money(13_500), new Money(103_500),
            [
                new InvoiceLineSnapshot(
                    Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Consulting", "SKU-1", "hour", 2m,
                    new Money(50_000), VatCategory.Standard15, null, null,
                    new Money(90_000), new Money(13_500), new Money(103_500)),
                new InvoiceLineSnapshot(
                    Guid.NewGuid(), null, Guid.NewGuid(), "Exempt service", null, "unit", 1m,
                    new Money(0), VatCategory.Exempt, "VATEX-SA-29", "Qualifying exemption",
                    Money.Zero, Money.Zero, Money.Zero),
            ],
            null);

    private static string Format(InvoiceSnapshot snapshot)
    {
        Type formatter = typeof(CanonicalInvoicePdfActions).Assembly.GetType(
            "MHC.Invoicing.App.Documents.InvoiceAccessibleTextFormatter",
            throwOnError: true)!;
        MethodInfo method = formatter.GetMethod("Format", BindingFlags.Static | BindingFlags.NonPublic)!;
        return Assert.IsType<string>(method.Invoke(null, [snapshot]));
    }

    private static void SetLanguageForTest(string language)
    {
        FieldInfo field = typeof(LocalizationState).GetField(
            "_language",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        field.SetValue(null, language);
    }
}

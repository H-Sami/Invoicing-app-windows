using System.Text.RegularExpressions;
using System.Xml.Linq;
using MHC.Invoicing.App.Localization;

namespace MHC.Invoicing.Ui.Tests;

public sealed class LocalizationResourceTests
{
    [Fact]
    public void StartupFailureTitlePreservesBuildFlavorIdentity()
    {
        string failureTitle = ReadIdentityConstant(typeof(MHC.Invoicing.App.App), "StartupFailureWindowTitle");
        string mutexName = ReadIdentityConstant(typeof(MHC.Invoicing.App.App), "InstanceMutexName");
        string windowTitle = ReadIdentityConstant(typeof(MHC.Invoicing.App.MainWindow), "WindowTitle");
#if LOCAL_QA
        Assert.Contains("LOCAL QA", failureTitle, StringComparison.Ordinal);
        Assert.Contains("LocalQA", mutexName, StringComparison.Ordinal);
        Assert.Contains("LOCAL QA", windowTitle, StringComparison.Ordinal);
#else
        Assert.DoesNotContain("LOCAL QA", failureTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalQA", mutexName, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCAL QA", windowTitle, StringComparison.Ordinal);
#endif
        Assert.Contains("Startup failure", failureTitle, StringComparison.Ordinal);
    }

    private static string ReadIdentityConstant(Type type, string name) =>
        Assert.IsType<string>(type.GetField(
            name,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetRawConstantValue());

    [Fact]
    public void SetLanguage_WorksWithoutPackageIdentityAndUpdatesSelectedLanguage()
    {
        LocalizationState.SetLanguage("en-US");

        Assert.Equal("en-US", LocalizationState.Language);
    }

    [Theory]
    [InlineData("TextBox", "DefaultTextBoxStyle")]
    [InlineData("AutoSuggestBox", "DefaultAutoSuggestBoxStyle")]
    [InlineData("NumberBox", null)]
    public void EditableControlsAcceptEnglishInputWithoutChangingApplicationLanguage(
        string targetType,
        string? defaultStyleKey)
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MHC.Invoicing.App",
            "Styles",
            "Controls.xaml");
        XDocument controls = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement style = Assert.Single(
            controls.Root!.Elements(presentation + "Style"),
            element => (string?)element.Attribute("TargetType") == targetType);

        if (defaultStyleKey is null)
        {
            Assert.Null((string?)style.Attribute("BasedOn"));
        }
        else
        {
            Assert.Equal(
                $"{{StaticResource {defaultStyleKey}}}",
                (string?)style.Attribute("BasedOn"));
        }
        XElement language = Assert.Single(
            style.Elements(presentation + "Setter"),
            element => (string?)element.Attribute("Property") == "Language");
        Assert.Equal("en-US", (string?)language.Attribute("Value"));
    }

    [Fact]
    public void ArabicAndEnglishResourcesHaveIdenticalKeys()
    {
        string repositoryRoot = FindRepositoryRoot();
        Dictionary<string, string> arabic = LoadResources(Path.Combine(
            repositoryRoot,
            "src",
            "MHC.Invoicing.App",
            "Strings",
            "ar-SA",
            "Resources.resw"));
        Dictionary<string, string> english = LoadResources(Path.Combine(
            repositoryRoot,
            "src",
            "MHC.Invoicing.App",
            "Strings",
            "en-US",
            "Resources.resw"));

        Assert.NotEmpty(arabic);
        Assert.Equal(arabic.Keys.OrderBy(key => key), english.Keys.OrderBy(key => key));
        Assert.All(arabic.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.All(english.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        foreach (string key in arabic.Keys)
        {
            string[] arabicPlaceholders = FormatPlaceholders(arabic[key]);
            string[] englishPlaceholders = FormatPlaceholders(english[key]);
            Assert.Equal(arabicPlaceholders, englishPlaceholders);
        }
    }

    [Fact]
    public void ProductionXamlUsesOnlyKnownExplicitLocalizationKeys()
    {
        string repositoryRoot = FindRepositoryRoot();
        string appRoot = Path.Combine(repositoryRoot, "src", "MHC.Invoicing.App");
        HashSet<string> keys = LoadKeys(Path.Combine(appRoot, "Strings", "ar-SA", "Resources.resw"));
        string[] xamlFiles = Directory.GetFiles(Path.Combine(appRoot, "Pages"), "*.xaml")
            .Append(Path.Combine(appRoot, "MainWindow.xaml"))
            .ToArray();

        foreach (string xamlFile in xamlFiles)
        {
            string xaml = File.ReadAllText(xamlFile);
            Assert.DoesNotContain("x:Uid=", xaml, StringComparison.Ordinal);
            foreach (Match match in Regex.Matches(
                xaml,
                "localization:Localization\\.(?:Text|Content|Placeholder|Header|AutomationName)Key=\"(?<key>[^\"]+)\"",
                RegexOptions.CultureInvariant))
            {
                Assert.Contains(match.Groups["key"].Value, keys);
            }
        }
    }

    [Theory]
    [InlineData("InvoiceEditorPage.xaml")]
    [InlineData("InvoicesPage.xaml")]
    [InlineData("SettingsPage.xaml")]
    [InlineData("DashboardPage.xaml")]
    [InlineData("CustomersPage.xaml")]
    [InlineData("ItemsPage.xaml")]
    public void WorkflowXamlHasNoHardCodedUserVisibleText(string fileName)
    {
        string path = Path.Combine(FindRepositoryRoot(), "src", "MHC.Invoicing.App", "Pages", fileName);
        string xaml = File.ReadAllText(path);
        MatchCollection literals = Regex.Matches(
            xaml,
            "(?:Text|Content|PlaceholderText|Header|Title|Message|AutomationProperties\\.Name)=\"(?!\\{)(?<value>[^\"]*[A-Za-z\\u0600-\\u06FF][^\"]*)\"",
            RegexOptions.CultureInvariant);

        Assert.Empty(literals.Cast<Match>().Select(match => match.Groups["value"].Value));
    }

    [Theory]
    [InlineData("InvoiceEditorPage.xaml.cs")]
    [InlineData("InvoicesPage.xaml.cs")]
    [InlineData("SettingsPage.xaml.cs")]
    public void WorkflowCodeBehindUsesResourcesForOwnedUserVisibleText(string fileName)
    {
        string repositoryRoot = FindRepositoryRoot();
        string appRoot = Path.Combine(repositoryRoot, "src", "MHC.Invoicing.App");
        string source = File.ReadAllText(Path.Combine(appRoot, "Pages", fileName));
        HashSet<string> keys = LoadKeys(Path.Combine(appRoot, "Strings", "ar-SA", "Resources.resw"));

        Assert.DoesNotMatch("[\\u0600-\\u06FF]", source);
        foreach (Match match in Regex.Matches(
            source,
            "(?:LocalizationState\\.GetString|L)\\(\"(?<key>[^\"]+)\"\\)",
            RegexOptions.CultureInvariant))
        {
            Assert.Contains(match.Groups["key"].Value, keys);
        }
    }

    [Fact]
    public void UserFacingWorkflowSourceDoesNotExposeRawExceptionOrValidationMessages()
    {
        string pages = Path.Combine(FindRepositoryRoot(), "src", "MHC.Invoicing.App", "Pages");
        foreach (string fileName in new[]
                 {
                     "InvoiceEditorPage.xaml.cs", "InvoicesPage.xaml.cs", "SettingsPage.xaml.cs",
                 })
        {
            string source = File.ReadAllText(Path.Combine(pages, fileName));
            Assert.DoesNotContain("exception.Message", source, StringComparison.Ordinal);
            Assert.DoesNotContain("exception.InnerException?.Message", source, StringComparison.Ordinal);
            Assert.DoesNotContain("error.Message", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InvoiceTotalsUseMirroredColumnsInsteadOfOverlappingCells()
    {
        string path = Path.Combine(
            FindRepositoryRoot(), "src", "MHC.Invoicing.App", "Pages", "InvoiceEditorPage.xaml");
        string xaml = File.ReadAllText(path);

        Assert.Equal(3, Regex.Count(
            xaml,
            "x:Name=\"(?:Subtotal|Vat|GrandTotal)Text\" Grid.Column=\"1\""));
    }

    [Fact]
    public void InvoiceEditorRequiresExplicitPaymentAndUsesPlainHyphensInSuggestions()
    {
        string pages = Path.Combine(FindRepositoryRoot(), "src", "MHC.Invoicing.App", "Pages");
        XDocument editor = XDocument.Load(Path.Combine(pages, "InvoiceEditorPage.xaml"));
        XElement payment = Assert.Single(
            editor.Descendants(),
            element => (string?)element.Attribute("AutomationProperties.AutomationId") == "Invoice.PaymentMethod");
        Assert.Equal("PaymentMethodPicker_SelectionChanged", (string?)payment.Attribute("SelectionChanged"));
        string settings = File.ReadAllText(Path.Combine(pages, "SettingsPage.xaml"));
        Assert.DoesNotContain("DefaultPaymentMethodPicker", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.DefaultPaymentMethod", settings, StringComparison.Ordinal);

        string source = File.ReadAllText(Path.Combine(pages, "InvoiceEditorPage.xaml.cs"));
        int customerStart = source.IndexOf("private sealed record CustomerChoice", StringComparison.Ordinal);
        int catalogStart = source.IndexOf("private sealed record CatalogChoice", customerStart, StringComparison.Ordinal);
        int adapterStart = source.IndexOf("private sealed class LookupAdapter", catalogStart, StringComparison.Ordinal);
        Assert.True(customerStart >= 0 && catalogStart > customerStart && adapterStart > catalogStart);
        string suggestions = source[customerStart..adapterStart];
        Assert.DoesNotContain(" — ", suggestions, StringComparison.Ordinal);
        Assert.Contains("$\"{name} - {Customer.VatNumber}\"", suggestions, StringComparison.Ordinal);
        Assert.Contains("$\"{name} - {FormatMoney(Item.DefaultUnitPrice)}\"", suggestions, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardRecentInvoicesUseStructuredBidiSafeFields()
    {
        string path = Path.Combine(
            FindRepositoryRoot(), "src", "MHC.Invoicing.App", "Pages", "DashboardPage.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XDocument dashboard = XDocument.Load(path);
        XElement list = Assert.Single(
            dashboard.Descendants(presentation + "ListView"),
            element => (string?)element.Attribute("AutomationProperties.AutomationId") == "Dashboard.RecentInvoices");
        XElement template = Assert.Single(list.Descendants(presentation + "DataTemplate"));
        string templateText = template.ToString(SaveOptions.DisableFormatting);
        foreach (string binding in new[] { "PublicNumber", "TypeAndStatus", "CustomerName", "BusinessDate", "Amount" })
        {
            Assert.Contains($"{{Binding {binding}}}", templateText, StringComparison.Ordinal);
        }

        Assert.True(
            template.Descendants(presentation + "TextBlock").Count(
                element => (string?)element.Attribute("FlowDirection") == "LeftToRight") >= 3);
    }

    [Fact]
    public void DashboardFailureStateAndIdentifierEditorLimitsAreExplicit()
    {
        string pages = Path.Combine(FindRepositoryRoot(), "src", "MHC.Invoicing.App", "Pages");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument dashboard = XDocument.Load(Path.Combine(pages, "DashboardPage.xaml"));
        XElement error = Assert.Single(
            dashboard.Descendants(),
            element => (string?)element.Attribute(x + "Name") == "DashboardLoadError");
        Assert.Equal("Dashboard.LoadError", (string?)error.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("Assertive", (string?)error.Attribute("AutomationProperties.LiveSetting"));
        Assert.Equal("Collapsed", (string?)error.Attribute("Visibility"));

        string dashboardCode = File.ReadAllText(Path.Combine(pages, "DashboardPage.xaml.cs"));
        Assert.Contains("InvoiceCountValue.Text = \"—\";", dashboardCode, StringComparison.Ordinal);
        Assert.Contains("DashboardLoadError.Visibility = Visibility.Visible;", dashboardCode, StringComparison.Ordinal);
        Assert.Contains("Invoice.DocumentType == InvoiceDocumentType.CreditNote", dashboardCode, StringComparison.Ordinal);
        Assert.Contains("Invoice.IsVoided", dashboardCode, StringComparison.Ordinal);
        Assert.Contains("InvoiceStatus.Voided", dashboardCode, StringComparison.Ordinal);

        foreach ((string fileName, string controlName) in new[]
                 {
                     ("SettingsPage.xaml", "CompanyVatNumber"),
                     ("SettingsPage.xaml", "CompanyCommercialRegistration"),
                     ("InvoiceEditorPage.xaml", "CustomerVatNumber"),
                     ("InvoiceEditorPage.xaml", "CustomerCommercialRegistration"),
                 })
        {
            XDocument document = XDocument.Load(Path.Combine(pages, fileName));
            XElement editor = Assert.Single(
                document.Descendants(),
                element => (string?)element.Attribute(x + "Name") == controlName);
            Assert.Equal("50", (string?)editor.Attribute("MaxLength"));
        }
    }

    [Fact]
    public void ItemEditorPopulatesVatOptionsBeforeSelectionAndBoundsDialogContent()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(
            Path.Combine(root, "src", "MHC.Invoicing.App", "Pages", "ItemsPage.xaml.cs"));
        string controls = File.ReadAllText(
            Path.Combine(root, "src", "MHC.Invoicing.App", "Styles", "Controls.xaml"));

        int finalOption = source.LastIndexOf("vat.Items.Add(", StringComparison.Ordinal);
        int selection = source.IndexOf("vat.SelectedIndex =", StringComparison.Ordinal);
        Assert.True(finalOption >= 0 && selection > finalOption);
        Assert.Contains(
            "Content = new ScrollViewer { Content = fields, MaxHeight = 520 },",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultNumberBoxStyle", controls, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationPreferencesCanBeSavedWithoutACompanyProfile()
    {
        string pages = Path.Combine(FindRepositoryRoot(), "src", "MHC.Invoicing.App", "Pages");
        string source = File.ReadAllText(Path.Combine(pages, "SettingsPage.xaml.cs"));
        int preferencesStart = source.IndexOf("private async void SavePreferences_Click", StringComparison.Ordinal);
        int preferencesEnd = source.IndexOf("private async void SaveCompany_Click", preferencesStart, StringComparison.Ordinal);
        Assert.True(preferencesStart >= 0 && preferencesEnd > preferencesStart);
        string preferencesHandler = source[preferencesStart..preferencesEnd];
        Assert.DoesNotContain("CompanyProfileSettings", preferencesHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("CompanyProfileRepository", preferencesHandler, StringComparison.Ordinal);
        Assert.Contains("ApplyPreferencesAsync", preferencesHandler, StringComparison.Ordinal);

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument xaml = XDocument.Load(Path.Combine(pages, "SettingsPage.xaml"));
        Assert.Single(
            xaml.Descendants(),
            element => (string?)element.Attribute("AutomationProperties.AutomationId") == "Settings.SaveCompany"
                && (string?)element.Attribute("Click") == "SaveCompany_Click");
        Assert.Single(
            xaml.Descendants(),
            element => (string?)element.Attribute("AutomationProperties.AutomationId") == "Settings.SavePreferences"
                && (string?)element.Attribute("Click") == "SavePreferences_Click");
    }

    [Fact]
    public void WindowAndInstallerUseVersionedSuppliedBrandingWithoutSubtitle()
    {
        string root = FindRepositoryRoot();
        string app = Path.Combine(root, "src", "MHC.Invoicing.App");
        string windowXaml = File.ReadAllText(Path.Combine(app, "MainWindow.xaml"));
        string windowCode = File.ReadAllText(Path.Combine(app, "MainWindow.xaml.cs"));
        string project = File.ReadAllText(Path.Combine(app, "MHC.Invoicing.App.csproj"));
        string installer = File.ReadAllText(Path.Combine(root, "installer", "MHC.Invoicing.iss"));

        Assert.DoesNotContain("AppSubtitle.Text", windowXaml, StringComparison.Ordinal);
        Assert.Contains("AppWindow.SetIcon", windowCode, StringComparison.Ordinal);
        Assert.Contains("MHCLogo-20260729.ico", windowCode, StringComparison.Ordinal);
        Assert.Contains("<ApplicationIcon>Assets\\MHCLogo-20260729.ico</ApplicationIcon>", project, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Count(installer, "MHCLogo-20260729\\.ico"));
    }

    private static HashSet<string> LoadKeys(string path) =>
        LoadResources(path).Keys.ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> LoadResources(string path) =>
        XDocument.Load(path)
            .Root!
            .Elements("data")
            .ToDictionary(
                element => (string?)element.Attribute("name")
                    ?? throw new InvalidDataException($"A resource in {path} has no name."),
                element => element.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);

    private static string[] FormatPlaceholders(string value) =>
        Regex.Matches(value, "\\{\\d+(?::[^}]+)?\\}", RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .OrderBy(placeholder => placeholder, StringComparer.Ordinal)
            .ToArray();

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MHC.Invoicing.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

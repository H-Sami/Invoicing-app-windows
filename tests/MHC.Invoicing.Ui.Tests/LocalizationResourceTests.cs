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
    [InlineData("NumberBox", "DefaultNumberBoxStyle")]
    public void EditableControlsAcceptEnglishInputWithoutChangingApplicationLanguage(
        string targetType,
        string defaultStyleKey)
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

        Assert.Equal(
            $"{{StaticResource {defaultStyleKey}}}",
            (string?)style.Attribute("BasedOn"));
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

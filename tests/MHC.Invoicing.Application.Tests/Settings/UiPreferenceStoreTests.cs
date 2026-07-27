using MHC.Invoicing.Application.Settings;

namespace MHC.Invoicing.Application.Tests.Settings;

public sealed class UiPreferenceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mhc-preferences-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadAsync_WhenFileIsMissing_ReturnsArabicSystemDefaults()
    {
        UiPreferenceStore store = new(Path.Combine(_directory, "preferences.json"));

        UiPreferences result = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ar-SA", result.Language);
        Assert.Equal(UiTheme.System, result.Theme);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsValidatedPreferences()
    {
        UiPreferenceStore store = new(Path.Combine(_directory, "preferences.json"));
        UiPreferences expected = new("en-US", UiTheme.Dark);

        await store.SaveAsync(expected, TestContext.Current.CancellationToken);
        UiPreferences actual = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("")]
    public void Constructor_RejectsUnsupportedLanguage(string language)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiPreferences(language, UiTheme.Light));
    }

    [Fact]
    public async Task LoadAsync_WhenFileIsMalformed_ReturnsSafeDefaults()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "preferences.json");
        await File.WriteAllTextAsync(path, "{not-json", TestContext.Current.CancellationToken);
        UiPreferenceStore store = new(path);

        UiPreferences result = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UiPreferences.Default, result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}

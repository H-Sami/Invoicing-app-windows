using System.Text.Json;

namespace MHC.Invoicing.Application.Settings;

public enum UiTheme
{
    System,
    Light,
    Dark,
}

public sealed record UiPreferences
{
    public UiPreferences(string language, UiTheme theme)
    {
        if (language is not ("ar-SA" or "en-US"))
            throw new ArgumentOutOfRangeException(nameof(language), language, "Only Arabic and English are supported.");
        if (!Enum.IsDefined(theme))
            throw new ArgumentOutOfRangeException(nameof(theme));

        Language = language;
        Theme = theme;
    }

    public static UiPreferences Default { get; } = new("ar-SA", UiTheme.System);

    public string Language { get; }

    public UiTheme Theme { get; }
}

public sealed class UiPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly string _path;

    public UiPreferenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<UiPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return UiPreferences.Default;

        try
        {
            await using FileStream stream = new(
                _path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            PreferenceFile? file = await JsonSerializer.DeserializeAsync<PreferenceFile>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return file is null
                ? UiPreferences.Default
                : new UiPreferences(file.Language ?? string.Empty, file.Theme);
        }
        catch (JsonException)
        {
            return UiPreferences.Default;
        }
        catch (ArgumentException)
        {
            return UiPreferences.Default;
        }
        catch (IOException)
        {
            return UiPreferences.Default;
        }
    }

    public async Task SaveAsync(UiPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                PreferenceFile file = new(preferences.Language, preferences.Theme);
                await JsonSerializer.SerializeAsync(stream, file, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed record PreferenceFile(string? Language, UiTheme Theme);
}

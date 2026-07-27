using Microsoft.Windows.ApplicationModel.Resources;

namespace MHC.Invoicing.App.Localization;

public static class LocalizationState
{
    private static readonly Lock Sync = new();
    private static string _language = "ar-SA";

    public static string Language
    {
        get
        {
            lock (Sync)
            {
                return _language;
            }
        }
    }

    public static void SetLanguage(string language)
    {
        if (language is not ("ar-SA" or "en-US"))
        {
            throw new ArgumentOutOfRangeException(nameof(language));
        }

        lock (Sync)
        {
            _language = language;
        }
    }

    internal static string GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ResourceManager manager = new();
        ResourceContext context = manager.CreateResourceContext();
        context.QualifierValues["Language"] = Language;
        ResourceMap resources = manager.MainResourceMap.GetSubtree("Resources");
        string resourceKey = key.Replace('.', '/');
        ResourceCandidate? candidate = resources.TryGetValue(resourceKey, context);
        return candidate?.ValueAsString ?? $"[{key}]";
    }
}

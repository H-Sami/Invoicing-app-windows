namespace MHC.Invoicing.Application.Preview;

/// <summary>
/// Defines the URI boundary for invoice HTML hosted with WebView2 NavigateToString.
/// </summary>
public static class InvoiceWebContentPolicy
{
    public static bool IsAllowedDocumentNavigation(string? uri) =>
        string.Equals(uri, "about:blank", StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedResource(string? uri) =>
        IsAllowedDocumentNavigation(uri) ||
        (uri is not null &&
            (uri.StartsWith("data:image/png", StringComparison.OrdinalIgnoreCase) ||
             uri.StartsWith("data:image/jpeg", StringComparison.OrdinalIgnoreCase)));
}

public sealed class InternalDataNavigationGrant
{
    public const string ExpectedPrefix = "data:text/html;charset=utf-8;base64,";
    private int _armed;

    public void Arm() => Interlocked.Exchange(ref _armed, 1);

    public void Cancel() => Interlocked.Exchange(ref _armed, 0);

    public bool TryConsume(string? uri)
    {
        if (uri is null || !uri.StartsWith(ExpectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _armed, 0, 1) == 1;
    }
}

using MHC.Invoicing.Application.Preview;

namespace MHC.Invoicing.Application.Tests.Preview;

public sealed class InvoiceWebContentPolicyTests
{
    [Theory]
    [InlineData("about:blank")]
    [InlineData("ABOUT:BLANK")]
    public void AllowsOnlyNavigateToStringDocumentNavigation(string uri)
    {
        Assert.True(InvoiceWebContentPolicy.IsAllowedDocumentNavigation(uri));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("about:blank#fragment")]
    [InlineData("data:text/html,<h1>invoice</h1>")]
    [InlineData("file:///C:/invoice.html")]
    [InlineData("https://example.test/invoice")]
    [InlineData("javascript:alert(1)")]
    public void RejectsUnexpectedDocumentNavigation(string? uri)
    {
        Assert.False(InvoiceWebContentPolicy.IsAllowedDocumentNavigation(uri));
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("ABOUT:BLANK")]
    [InlineData("data:image/png;base64,AA==")]
    [InlineData("data:image/jpeg;base64,AA==")]
    public void AllowsNavigateToStringDocumentAndEmbeddedDataResources(string uri)
    {
        Assert.True(InvoiceWebContentPolicy.IsAllowedResource(uri));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///C:/logo.png")]
    [InlineData("https://example.test/logo.png")]
    [InlineData("http://127.0.0.1/logo.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:font/woff2;base64,AA==")]
    [InlineData("data:image/svg+xml;base64,AA==")]
    [InlineData("data:text/html;base64,AA==")]
    public void RejectsNonEmbeddedResources(string? uri)
    {
        Assert.False(InvoiceWebContentPolicy.IsAllowedResource(uri));
    }

    [Fact]
    public void InternalDataNavigationGrantIsConsumedByOnlyTheFirstMatchingNavigation()
    {
        InternalDataNavigationGrant grant = new();
        grant.Arm();

        Assert.False(grant.TryConsume("https://example.test"));
        Assert.True(grant.TryConsume("data:text/html;charset=utf-8;base64,AA=="));
        Assert.False(grant.TryConsume("data:text/html;charset=utf-8;base64,BB=="));
    }
}

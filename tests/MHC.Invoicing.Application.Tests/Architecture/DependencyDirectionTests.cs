namespace MHC.Invoicing.Application.Tests.Architecture;

public sealed class DependencyDirectionTests
{
    [Fact]
    public void Application_ReferencesDomainButNotOuterLayers()
    {
        string project = ReadProject("src", "MHC.Invoicing.Application", "MHC.Invoicing.Application.csproj");

        Assert.Contains("MHC.Invoicing.Domain.csproj", project, StringComparison.Ordinal);
        Assert.DoesNotContain("MHC.Invoicing.Infrastructure", project, StringComparison.Ordinal);
        Assert.DoesNotContain("MHC.Invoicing.App.csproj", project, StringComparison.Ordinal);
    }

    private static string ReadProject(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MHC.Invoicing.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory.FullName, .. segments]));
    }
}

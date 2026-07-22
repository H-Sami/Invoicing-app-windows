namespace MHC.Invoicing.Infrastructure.Tests.Architecture;

public sealed class DependencyDirectionTests
{
    [Fact]
    public void Infrastructure_ReferencesApplicationAndDomainButNotUi()
    {
        string project = ReadProject("src", "MHC.Invoicing.Infrastructure", "MHC.Invoicing.Infrastructure.csproj");

        Assert.Contains("MHC.Invoicing.Application.csproj", project, StringComparison.Ordinal);
        Assert.Contains("MHC.Invoicing.Domain.csproj", project, StringComparison.Ordinal);
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

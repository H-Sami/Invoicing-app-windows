namespace MHC.Invoicing.Domain.Tests.Architecture;

public sealed class DependencyDirectionTests
{
    [Fact]
    public void DomainProject_HasNoProjectDependencies()
    {
        string projectFile = FindProjectFile("src", "MHC.Invoicing.Domain", "MHC.Invoicing.Domain.csproj");
        string projectText = File.ReadAllText(projectFile);

        Assert.DoesNotContain("<ProjectReference", projectText, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindProjectFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {segments[^1]} from the test output directory.");
    }
}

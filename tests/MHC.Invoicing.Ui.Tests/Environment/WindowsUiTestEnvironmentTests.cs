namespace MHC.Invoicing.Ui.Tests.Environment;

public sealed class WindowsUiTestEnvironmentTests
{
    [Fact]
    public void UiAutomationSuite_RunsOnWindows()
    {
        Assert.True(OperatingSystem.IsWindows(), "WinUI desktop automation requires Windows.");
    }
}

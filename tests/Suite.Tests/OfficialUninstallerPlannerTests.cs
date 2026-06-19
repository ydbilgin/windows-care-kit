using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using Xunit;

namespace WindowsCareKit.Tests;

public class OfficialUninstallerPlannerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Prefers_quiet_uninstall_string()
    {
        var app = TestData.App(
            uninstall: "\"C:\\App\\u.exe\"",
            quietUninstall: "\"C:\\App\\u.exe\" /S");

        var plan = OfficialUninstallerPlanner.Build(app, T0);

        Assert.NotNull(plan);
        var cmd = Assert.IsType<CommandAction>(plan!.Actions.Single());
        Assert.Equal(@"C:\App\u.exe", cmd.FileName);
        Assert.Equal(new[] { "/S" }, cmd.Arguments);
    }

    [Fact]
    public void Machine_wide_app_requires_elevation()
    {
        var app = TestData.App(source: InstalledAppSource.MachineWide64, uninstall: "\"C:\\App\\u.exe\"");
        var cmd = (CommandAction)OfficialUninstallerPlanner.Build(app, T0)!.Actions.Single();
        Assert.True(cmd.RequiresElevation);
    }

    [Fact]
    public void Per_user_app_does_not_require_elevation()
    {
        var app = TestData.App(source: InstalledAppSource.CurrentUser, uninstall: "\"C:\\App\\u.exe\"");
        var cmd = (CommandAction)OfficialUninstallerPlanner.Build(app, T0)!.Actions.Single();
        Assert.False(cmd.RequiresElevation);
    }

    [Fact]
    public void Returns_null_when_no_uninstall_string()
        => Assert.Null(OfficialUninstallerPlanner.Build(TestData.App(), T0));

    [Fact]
    public void Produced_command_passes_the_safety_gate()
    {
        var app = TestData.App(uninstall: "\"C:\\Program Files\\App\\unins000.exe\" /SILENT");
        var plan = OfficialUninstallerPlanner.Build(app, T0)!;
        Assert.True(TestData.Gate().Validate(plan).AllAllowed);
    }
}

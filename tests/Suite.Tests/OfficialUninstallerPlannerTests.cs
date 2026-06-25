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
        // Machine-wide (elevated) → Phase 2 anchor requires a matching InstallLocation; the exe sits under it.
        var app = TestData.App(
            uninstall: "\"C:\\App\\u.exe\"",
            quietUninstall: "\"C:\\App\\u.exe\" /S",
            installLocation: @"C:\App");

        var plan = OfficialUninstallerPlanner.Build(app, T0);

        Assert.NotNull(plan);
        var cmd = Assert.IsType<CommandAction>(plan!.Actions.Single());
        Assert.Equal(@"C:\App\u.exe", cmd.FileName);
        Assert.Equal(new[] { "/S" }, cmd.Arguments);
    }

    [Fact]
    public void Machine_wide_app_requires_elevation()
    {
        // Machine-wide → elevated; the Phase 2 anchor is satisfied by a matching InstallLocation.
        var app = TestData.App(source: InstalledAppSource.MachineWide64,
            uninstall: "\"C:\\App\\u.exe\"", installLocation: @"C:\App");
        var cmd = (CommandAction)OfficialUninstallerPlanner.Build(app, T0)!.Actions.Single();
        Assert.True(cmd.RequiresElevation);
    }

    [Fact]
    public void Per_user_app_does_not_require_elevation()
    {
        // Per-user → non-elevated; no anchor is required (Phase 2 only anchors elevated uninstallers).
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
        var app = TestData.App(uninstall: "\"C:\\Program Files\\App\\unins000.exe\" /SILENT",
            installLocation: @"C:\Program Files\App");
        var plan = OfficialUninstallerPlanner.Build(app, T0)!;
        Assert.True(TestData.Gate().Validate(plan).AllAllowed);
    }

    // ---- Command-policy hardening PHASE 1 (C): reject a script/dangerous-extension uninstaller ----
    // No legitimate vendor uninstaller (L1-L7) is a script. A registry UninstallString pointing at a
    // .bat/.cmd/.ps1/.vbs/… wrapper must NOT become an official-uninstaller action — Build returns null so the
    // caller falls back to leftover-cleanup. Here .bat/.cmd ARE rejected (npm does not flow through this path).

    [Theory]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".ps1")]
    [InlineData(".vbs")]
    [InlineData(".vbe")]
    [InlineData(".js")]
    [InlineData(".jse")]
    [InlineData(".wsf")]
    [InlineData(".wsh")]
    [InlineData(".hta")]
    [InlineData(".scr")]
    [InlineData(".msc")]
    [InlineData(".com")]
    [InlineData(".pif")]
    [InlineData(".cpl")]
    public void PhaseC_returns_null_for_a_script_extension_uninstaller(string ext)
    {
        var app = TestData.App(uninstall: $"\"C:\\Program Files\\App\\uninstall{ext}\" /S");
        Assert.Null(OfficialUninstallerPlanner.Build(app, T0));
    }

    [Fact]
    public void PhaseC_returns_null_for_an_unquoted_cmd_wrapper_with_args()
    {
        // Unquoted form (no .exe boundary) still parses the file name and is rejected by extension.
        var app = TestData.App(uninstall: @"C:\ProgramData\App\remove.cmd /quiet");
        Assert.Null(OfficialUninstallerPlanner.Build(app, T0));
    }

    [Fact]
    public void PhaseC_still_builds_a_real_exe_uninstaller()
    {
        // Paired ALLOW: an ordinary .exe uninstaller is unaffected and still produces a gate-clean plan.
        var app = TestData.App(uninstall: "\"C:\\Program Files\\App\\unins000.exe\" /SILENT",
            installLocation: @"C:\Program Files\App");
        var plan = OfficialUninstallerPlanner.Build(app, T0);
        Assert.NotNull(plan);
        Assert.True(TestData.Gate().Validate(plan!).AllAllowed);
    }

    [Fact]
    public void PhaseC_does_not_reject_msi_uninstall_even_though_registry_form_is_bare()
    {
        // The MSI branch is pinned to System32 msiexec.exe (a real .exe), so it is never script-rejected.
        var app = TestData.App(uninstall: "MsiExec.exe /X{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}");
        var plan = OfficialUninstallerPlanner.Build(app, T0);
        Assert.NotNull(plan);
        Assert.True(TestData.Gate().Validate(plan!).AllAllowed);
    }

    // ---- Command-policy PHASE 2 planner pre-flight (review fix): EXPANSION-FREE "can this ever anchor?" ----
    // The planner has no IPathCanonicalizer, so it must NOT run the gate's path-containment check on the RAW exe
    // (that over-blocks a registry-stored 8.3 short path the gate WOULD allow after 8.3 expansion). It returns null
    // (→ wizard manual fallback) ONLY when anchoring is impossible regardless of expansion (no usable InstallLocation
    // AND not the NSIS carve-out). The GATE stays the authoritative containment check on the expanded path.

    [Fact]
    public void Phase2_elevated_uninstaller_with_an_8_3_short_path_is_not_over_blocked()
    {
        // Review regression. Registry stores C:\PROGRA~1\App\unins000.exe (8.3); InstallLocation is the long
        // form. The planner must BUILD the action (a usable root exists) and defer the contained-or-block decision
        // to the gate's expanded-path check — it must NOT over-block here.
        var app = TestData.App(source: InstalledAppSource.MachineWide64,
            uninstall: "\"C:\\PROGRA~1\\App\\unins000.exe\" /S",
            installLocation: @"C:\Program Files\App");
        Assert.NotNull(OfficialUninstallerPlanner.Build(app, T0));
    }

    [Fact]
    public void Phase2_elevated_uninstaller_with_no_usable_install_location_falls_back_to_manual()
    {
        // Paired BLOCK (non-vacuous vs the over-block fix above): elevated + NO InstallLocation → anchoring is
        // impossible regardless of expansion → Build returns null → the wizard's manual fallback. Reverting the
        // preflight to always-build makes this FAIL.
        var app = TestData.App(source: InstalledAppSource.MachineWide64,
            uninstall: "\"C:\\PROGRA~1\\App\\unins000.exe\" /S");
        Assert.Null(OfficialUninstallerPlanner.Build(app, T0));
    }
}

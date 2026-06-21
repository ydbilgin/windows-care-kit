using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Command-policy PHASE 2 gate tests: the ADDITIVE, profile-keyed restriction layered AFTER the Phase-1 checks.
/// Each BLOCK rule has a paired ALLOW (over-block guard) and is non-vacuous (it FAILS when the rule is reverted —
/// proven in the implementation report). Per Fix 6 the anchor BLOCK uses a clean-stem exe OUTSIDE InstallLocation
/// (NOT vssadmin, which Phase-1 already blocks → vacuous), and the NSIS ALLOW fixture uses a realistic randomized
/// %TEMP%\~nsuA1B2.tmp\Au_.exe (a literal-dir fixture is vacuously green).
/// </summary>
public class SafetyGateCommandPolicyPhase2Tests
{
    private const string Guid = "{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}";

    private static CommandAction OfficialUninstaller(
        string fileName, bool elevated, string? allowedRoot, params string[] args)
        => new()
        {
            FileName = fileName,
            Arguments = args,
            RequiresElevation = elevated,
            Profile = CommandPolicyProfile.OfficialUninstaller,
            AllowedExecutableRoot = allowedRoot,
            Description = "official uninstaller",
            Reason = "test",
        };

    private static CommandAction Winget(string fileName, bool elevated, params string[] args)
        => new()
        {
            FileName = fileName,
            Arguments = args,
            RequiresElevation = elevated,
            Profile = CommandPolicyProfile.WingetInstall,
            Description = "winget install",
            Reason = "test",
        };

    private static CommandAction Npm(string fileName, bool elevated, params string[] args)
        => new()
        {
            FileName = fileName,
            Arguments = args,
            RequiresElevation = elevated,
            Profile = CommandPolicyProfile.NpmInstall,
            Description = "npm install",
            Reason = "test",
        };

    // ============================================================================================
    // BLOCK rule 1 — elevated OfficialUninstaller exe NOT anchored under its install directory.
    //   Fix 6: clean-stem exe outside InstallLocation (NOT vssadmin → that would be Phase-1 vacuous).
    // ============================================================================================

    [Fact]
    public void Blocks_elevated_official_uninstaller_with_a_clean_stem_exe_outside_install_location()
    {
        // setup-helper.exe is a perfectly clean stem (Phase-1 allows it) but it sits OUTSIDE the app's
        // InstallLocation → the Phase-2 anchor must block it. This is the non-vacuous anchor BLOCK.
        var v = TestData.Gate().Evaluate(OfficialUninstaller(
            @"C:\Temp\setup-helper.exe", elevated: true, allowedRoot: @"C:\Program Files\App", "/S"));
        Assert.False(v.Allowed, "elevated official uninstaller outside its install dir must be blocked: " + v.Reason);
    }

    [Fact]
    public void Blocks_elevated_official_au_exe_in_a_non_nsu_temp_dir()
    {
        // An attacker drops their own Au_.exe in an ordinary (non-~nsu) temp subdir. The NSIS carve-out must NOT
        // admit it (the dir leaf does not match ~nsu<HEX>.tmp), and it is not under any install root → block.
        string evil = Path.Combine(Path.GetTempPath(), "evil", "Au_.exe");
        var v = TestData.Gate().Evaluate(OfficialUninstaller(
            evil, elevated: true, allowedRoot: @"C:\Program Files\App", "/S"));
        Assert.False(v.Allowed, "Au_.exe in a non-~nsu temp dir must be blocked: " + v.Reason);
    }

    [Fact]
    public void Blocks_elevated_official_uninstaller_when_install_location_is_missing()
    {
        // A stale/missing InstallLocation (null anchor) cannot anchor an elevated uninstaller → block → manual.
        var v = TestData.Gate().Evaluate(OfficialUninstaller(
            @"C:\Program Files\App\unins000.exe", elevated: true, allowedRoot: null, "/S"));
        Assert.False(v.Allowed, "missing anchor must block the elevated uninstaller: " + v.Reason);
    }

    [Fact]
    public void Blocks_elevated_official_uninstaller_with_a_UNC_install_location()
    {
        // A UNC InstallLocation can never anchor (the exe is local, the root is remote) → block.
        var v = TestData.Gate().Evaluate(OfficialUninstaller(
            @"C:\Program Files\App\unins000.exe", elevated: true, allowedRoot: @"\\server\share\App", "/S"));
        Assert.False(v.Allowed, "UNC anchor must block the elevated uninstaller: " + v.Reason);
    }

    [Fact]
    public void Allows_elevated_official_uninstaller_at_an_8_3_short_path_after_expansion()
    {
        // cx nit + divergence proof: the registry stores the exe as an 8.3 short path. The gate must EXPAND it
        // (FakeCanonicalizer maps PROGRA~1 → Program Files) and THEN anchor-check, so the long-form InstallLocation
        // contains it → ALLOW. Non-vacuous: without the expansion, IsPathUnder(C:\PROGRA~1\App\..., C:\Program Files\App)
        // is false → block. Pairs with the planner test that the same 8.3 path is not over-blocked at preflight.
        var canon = new FakeCanonicalizer()
            .MapLongPath(@"C:\PROGRA~1\App\unins000.exe", @"C:\Program Files\App\unins000.exe");
        var v = TestData.Gate(canon).Evaluate(OfficialUninstaller(
            @"C:\PROGRA~1\App\unins000.exe", elevated: true, allowedRoot: @"C:\Program Files\App", "/S"));
        Assert.True(v.Allowed, "8.3 short-path uninstaller must be allowed after expansion: " + v.Reason);
    }

    [Fact]
    public void Blocks_spoofed_winget_claimed_as_an_official_uninstaller()
    {
        // A registry UninstallString points at winget.exe but is tagged OfficialUninstaller (NOT WingetInstall).
        // Without the WingetInstall profile it goes through the anchor rule; outside the install dir → block.
        var v = TestData.Gate().Evaluate(OfficialUninstaller(
            @"C:\Users\alice\AppData\Local\Microsoft\WindowsApps\winget.exe",
            elevated: true, allowedRoot: @"C:\Program Files\App", "install", "--id", "Evil.Pkg"));
        Assert.False(v.Allowed, "winget.exe smuggled as an official uninstaller must be blocked: " + v.Reason);
    }

    // ---- Paired ALLOW (over-block guard) for rule 1 ----

    [Fact]
    public void Allows_elevated_official_uninstaller_anchored_under_its_install_location()
    {
        var v = TestData.Gate().Evaluate(OfficialUninstaller(
            @"C:\Program Files\App\unins000.exe", elevated: true, allowedRoot: @"C:\Program Files\App", "/S"));
        Assert.True(v.Allowed, v.Reason);
    }

    [Fact]
    public void Allows_a_non_elevated_official_uninstaller_under_localappdata_without_an_anchor()
    {
        // Per-user (%LOCALAPPDATA%) uninstaller is NON-elevated → no anchor required (unaffected by Phase 2).
        string perUser = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "SomeApp", "Uninstall.exe");
        var v = TestData.Gate().Evaluate(OfficialUninstaller(perUser, elevated: false, allowedRoot: null, "/S"));
        Assert.True(v.Allowed, v.Reason);
    }

    // ============================================================================================
    // NSIS carve-out (Fix 2 + Fix 3) — randomized %TEMP%\~nsu<HEX>.tmp\Au_.exe ALLOWED; pinned dir+leaf.
    // ============================================================================================

    [Fact]
    public void Allows_elevated_official_nsis_uninstaller_from_a_randomized_nsu_temp_dir()
    {
        // Realistic randomized form: %TEMP%\~nsuA1B2.tmp\Au_.exe. It is NOT under the app install dir, so ONLY
        // the carve-out can admit it. (Fix 6: a literal "~nsu.tmp" fixture would be vacuous.)
        string nsis = Path.Combine(Path.GetTempPath(), "~nsuA1B2.tmp", "Au_.exe");
        var v = TestData.Gate().Evaluate(OfficialUninstaller(
            nsis, elevated: true, allowedRoot: @"C:\Program Files\App", "/S", @"_?=C:\Program Files\SomeApp"));
        Assert.True(v.Allowed, "randomized ~nsu<HEX>.tmp\\Au_.exe must be allowed: " + v.Reason);
    }

    [Fact]
    public void Blocks_a_wrong_leaf_in_a_valid_nsu_temp_dir()
    {
        // The dir matches ~nsu<HEX>.tmp but the leaf is NOT Au_.exe → both halves are pinned, so block.
        string wrongLeaf = Path.Combine(Path.GetTempPath(), "~nsuA1B2.tmp", "evil.exe");
        var v = TestData.Gate().Evaluate(OfficialUninstaller(
            wrongLeaf, elevated: true, allowedRoot: @"C:\Program Files\App", "/S"));
        Assert.False(v.Allowed, "a non-Au_.exe leaf in a ~nsu dir must be blocked: " + v.Reason);
    }

    [Fact]
    public void Blocks_nsis_au_exe_nested_deeper_than_directly_under_temp()
    {
        // ~nsu<HEX>.tmp must sit DIRECTLY under %TEMP% — a deeper-nested ~nsu dir is not the carve-out.
        string nested = Path.Combine(Path.GetTempPath(), "sub", "~nsuA1B2.tmp", "Au_.exe");
        var v = TestData.Gate().Evaluate(OfficialUninstaller(
            nested, elevated: true, allowedRoot: @"C:\Program Files\App", "/S"));
        Assert.False(v.Allowed, "a deeper-nested ~nsu Au_.exe must be blocked: " + v.Reason);
    }

    // ============================================================================================
    // System32-msiexec special-case — an OfficialUninstaller msiexec stays allowed when elevated (the
    // System32 pin from Phase-1 (D) is the control; the install-dir anchor does not apply to msiexec).
    // ============================================================================================

    [Fact]
    public void Allows_elevated_official_system32_msiexec_uninstall()
    {
        var v = TestData.Gate().Evaluate(OfficialUninstaller(
            @"C:\Windows\System32\msiexec.exe", elevated: true, allowedRoot: null, "/x", Guid, "/qn"));
        Assert.True(v.Allowed, v.Reason);
    }

    // ============================================================================================
    // BLOCK rule 2 — Winget/Npm profile bound to its exact resolver shape (forged tag on another exe → block).
    // ============================================================================================

    [Fact]
    public void Blocks_winget_profile_on_a_non_winget_executable()
    {
        // A forged WingetInstall tag on some other (clean-stem, rooted) exe must be rejected by the resolver pin.
        var v = TestData.Gate().Evaluate(Winget(
            @"C:\Temp\notwinget.exe", elevated: false, "install", "--id", "Evil.Pkg"));
        Assert.False(v.Allowed, "WingetInstall must require winget.exe: " + v.Reason);
    }

    [Fact]
    public void Blocks_npm_profile_on_a_non_npm_resolver()
    {
        // A forged NpmInstall tag on a different .cmd must be rejected (npm.cmd is the only admitted resolver).
        var v = TestData.Gate().Evaluate(Npm(
            @"C:\Program Files\nodejs\evil.cmd", elevated: false, "install", "-g", "--ignore-scripts", "pkg"));
        Assert.False(v.Allowed, "NpmInstall must require npm.cmd: " + v.Reason);
    }

    // ---- Paired ALLOW (over-block guard) for rule 2: real winget/npm resolvers stay green ----

    [Fact]
    public void Allows_winget_install_with_the_real_resolver()
    {
        var v = TestData.Gate().Evaluate(Winget(
            @"C:\Users\alice\AppData\Local\Microsoft\WindowsApps\winget.exe",
            elevated: false, "install", "--id", "Google.Chrome", "-e", "--silent"));
        Assert.True(v.Allowed, v.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Allows_npm_global_install_with_npm_cmd_elevated_and_non_elevated(bool elevated)
    {
        // npm .cmd is profile-admitted ONLY here (NpmInstall), elevated AND non-elevated.
        var v = TestData.Gate().Evaluate(Npm(
            @"C:\Program Files\nodejs\npm.cmd", elevated, "install", "-g", "--ignore-scripts", "typescript"));
        Assert.True(v.Allowed, v.Reason);
    }

    // ============================================================================================
    // Fix 5 proof — STRICTLY ADDITIVE: a Phase-1 deny STILL fires even with an OfficialUninstaller tag.
    //   An OfficialUninstaller tag can NEVER re-admit vssadmin / a .ps1 wrapper / a spoofed msiexec.
    // ============================================================================================

    [Fact]
    public void OfficialUninstaller_tag_does_not_re_admit_vssadmin()
    {
        // Even tagged OfficialUninstaller (with a satisfiable anchor), the Phase-1 deny-stem fires first.
        var v = TestData.Gate().Evaluate(OfficialUninstaller(
            @"C:\Windows\System32\vssadmin.exe", elevated: true, allowedRoot: @"C:\Windows\System32",
            "delete", "shadows", "/all", "/quiet"));
        Assert.False(v.Allowed, "vssadmin must stay blocked despite an OfficialUninstaller tag: " + v.Reason);
    }

    [Fact]
    public void OfficialUninstaller_tag_does_not_re_admit_a_ps1_wrapper()
    {
        // A .ps1 under the (matching) install dir is still blocked by the Phase-1 dangerous-extension check.
        var v = TestData.Gate().Evaluate(OfficialUninstaller(
            @"C:\Program Files\App\wrapper.ps1", elevated: true, allowedRoot: @"C:\Program Files\App", "/quiet"));
        Assert.False(v.Allowed, ".ps1 must stay blocked despite an OfficialUninstaller tag: " + v.Reason);
    }

    [Fact]
    public void OfficialUninstaller_tag_does_not_re_admit_a_spoofed_msiexec()
    {
        // A spoofed C:\Temp\msiexec.exe is blocked by the Phase-1 (D) System32 pin, tag notwithstanding.
        var v = TestData.Gate().Evaluate(OfficialUninstaller(
            @"C:\Temp\msiexec.exe", elevated: true, allowedRoot: @"C:\Temp", "/x", Guid));
        Assert.False(v.Allowed, "spoofed msiexec must stay blocked despite an OfficialUninstaller tag: " + v.Reason);
    }

    // ============================================================================================
    // Generic profile (the fail-safe default) is unchanged by Phase 2 — neither broadened nor narrowed.
    // ============================================================================================

    [Fact]
    public void Generic_command_is_unchanged_clean_exe_allowed()
    {
        var v = TestData.Gate().Evaluate(TestData.Command(@"C:\Program Files\App\unins000.exe", "/SILENT"));
        Assert.True(v.Allowed, v.Reason);
    }

    [Fact]
    public void Generic_command_still_blocks_a_lolbin()
    {
        var v = TestData.Gate().Evaluate(TestData.Command(@"C:\Windows\System32\vssadmin.exe", "delete", "shadows"));
        Assert.False(v.Allowed, v.Reason);
    }
}

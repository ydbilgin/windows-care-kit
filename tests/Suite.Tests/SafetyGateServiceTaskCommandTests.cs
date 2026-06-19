using WindowsCareKit.Core.Planning;
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

    [Fact]
    public void Allows_bare_msiexec_uninstall()
        => Assert.True(TestData.Gate().Evaluate(TestData.Command("msiexec.exe", "/x{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}")).Allowed);

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

    [Theory]
    [InlineData("msiexec.exe", "/i", "{0A1B2C3D-4E5F-6789-ABCD-EF0123456789}")]   // install verb
    [InlineData("msiexec.exe", "/i", "http://attacker/evil.msi")]                  // install + URL
    [InlineData("msiexec.exe", "/x{GUID}", "TRANSFORMS=evil.mst")]
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
}

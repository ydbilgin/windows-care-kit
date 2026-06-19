using WindowsCareKit.Core.Planning;
using Xunit;

namespace WindowsCareKit.Tests;

public class SafetyGateRegistryTests
{
    [Fact]
    public void Blocks_deleting_hive_root_key()
    {
        var v = TestData.Gate().Evaluate(TestData.RegKey(RegistryHive.LocalMachine, ""));
        Assert.False(v.Allowed);
    }

    [Theory]
    [InlineData("SOFTWARE")]
    [InlineData("SOFTWARE\\Microsoft")]
    [InlineData("SOFTWARE\\Microsoft\\Windows")]
    [InlineData("SOFTWARE\\Microsoft\\Windows\\CurrentVersion")]
    [InlineData("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion")]
    [InlineData("SOFTWARE\\Classes")]
    [InlineData("SOFTWARE\\WOW6432Node\\Microsoft\\Windows")]
    public void Blocks_deleting_protected_keys(string sub)
    {
        var v = TestData.Gate().Evaluate(TestData.RegKey(RegistryHive.LocalMachine, sub));
        Assert.False(v.Allowed, "should block key: " + sub);
    }

    [Theory]
    [InlineData("SYSTEM\\CurrentControlSet\\Services\\Foo")]
    [InlineData("SECURITY\\Policy")]
    [InlineData("SAM\\Domains")]
    public void Blocks_whole_subtree_roots(string sub)
    {
        var v = TestData.Gate().Evaluate(TestData.RegKey(RegistryHive.LocalMachine, sub));
        Assert.False(v.Allowed, "should block subtree: " + sub);
    }

    [Theory]
    [InlineData("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\SomeApp")]
    [InlineData("SOFTWARE\\SomeVendor\\App")]
    [InlineData("SOFTWARE\\WOW6432Node\\SomeVendor\\App")]
    public void Allows_deleting_app_remnant_keys(string sub)
    {
        var v = TestData.Gate().Evaluate(TestData.RegKey(RegistryHive.LocalMachine, sub));
        Assert.True(v.Allowed, v.Reason);
    }

    [Fact]
    public void Allows_deleting_a_run_value_for_startup_disable()
    {
        // Removing a single startup value (not the whole key) is what the startup manager will do.
        var v = TestData.Gate().Evaluate(
            TestData.RegValue(RegistryHive.CurrentUser, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", "SomeApp"));
        Assert.True(v.Allowed, v.Reason);
    }

    [Fact]
    public void Blocks_value_delete_inside_protected_subtree()
    {
        var v = TestData.Gate().Evaluate(
            TestData.RegValue(RegistryHive.LocalMachine, "SYSTEM\\Setup", "SomeValue"));
        Assert.False(v.Allowed);
    }

    [Fact]
    public void Registry_paths_are_case_and_separator_insensitive()
    {
        var v = TestData.Gate().Evaluate(TestData.RegKey(RegistryHive.LocalMachine, "\\software\\microsoft\\"));
        Assert.False(v.Allowed);
    }

    [Theory]
    [InlineData("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon", "Userinit")]
    [InlineData("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon", "Shell")]
    [InlineData("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\sethc.exe", "Debugger")]
    [InlineData("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Windows", "AppInit_DLLs")]
    [InlineData("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System", "EnableLUA")]
    public void Blocks_value_delete_in_boot_logon_critical_keys(string sub, string value)
    {
        var v = TestData.Gate().Evaluate(TestData.RegValue(RegistryHive.LocalMachine, sub, value));
        Assert.False(v.Allowed, $"should block value {value} in {sub}");
    }

    [Theory]
    [InlineData(RegistryHive.CurrentUser, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", "SomeApp")]
    [InlineData(RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce", "SomeApp")]
    [InlineData(RegistryHive.CurrentUser, "SOFTWARE\\SomeVendor\\App", "SomeSetting")]
    public void Allows_value_delete_in_startup_and_vendor_keys(RegistryHive hive, string sub, string value)
    {
        var v = TestData.Gate().Evaluate(TestData.RegValue(hive, sub, value));
        Assert.True(v.Allowed, v.Reason);
    }

    [Fact]
    public void Blocks_key_delete_of_winlogon()
    {
        var v = TestData.Gate().Evaluate(
            TestData.RegKey(RegistryHive.LocalMachine, "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon"));
        Assert.False(v.Allowed);
    }

    [Fact]
    public void Blocks_hku_winlogon_value_after_stripping_the_sid()
    {
        var v = TestData.Gate().Evaluate(TestData.RegValue(
            RegistryHive.Users,
            "S-1-5-21-1234567890-1\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon", "Userinit"));
        Assert.False(v.Allowed);
    }

    [Fact]
    public void Allows_hku_vendor_key_delete()
    {
        var v = TestData.Gate().Evaluate(TestData.RegKey(
            RegistryHive.Users, "S-1-5-21-1234567890-1\\Software\\SomeVendor\\App"));
        Assert.True(v.Allowed, v.Reason);
    }

    [Theory]
    [InlineData(RegistryHive.CurrentConfig)]
    public void Blocks_unmodeled_hive(RegistryHive hive)
        => Assert.False(TestData.Gate().Evaluate(TestData.RegKey(hive, "Anything\\Here")).Allowed);

    [Fact]
    public void Blocks_hku_without_a_sid()
        => Assert.False(TestData.Gate().Evaluate(TestData.RegKey(RegistryHive.Users, "SoftwareNoSid")).Allowed);
}

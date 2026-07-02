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
            TestData.CurrentUserSid + "\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon", "Userinit"));
        Assert.False(v.Allowed);
    }

    [Fact]
    public void Blocks_hku_other_user_sid()
    {
        var v = TestData.Gate().Evaluate(TestData.RegKey(
            RegistryHive.Users, TestData.OtherUserSid + "\\Software\\SomeVendor\\App"));
        Assert.False(v.Allowed);
    }

    [Fact]
    public void Allows_hku_current_user_vendor_key_delete()
    {
        var v = TestData.Gate().Evaluate(TestData.RegKey(
            RegistryHive.Users, TestData.CurrentUserSid + "\\Software\\SomeVendor\\App"));
        Assert.True(v.Allowed, v.Reason);
    }

    [Theory]
    [InlineData(".DEFAULT\\Software\\SomeVendor")]
    [InlineData("S-1-5-18\\Software\\SomeVendor")]
    [InlineData("S-1-5-19\\Software\\SomeVendor")]
    [InlineData("S-1-5-20\\Software\\SomeVendor")]
    public void Blocks_hku_template_and_service_sids(string subKey)
    {
        var v = TestData.Gate().Evaluate(TestData.RegKey(RegistryHive.Users, subKey));
        Assert.False(v.Allowed);
    }

    [Theory]
    [InlineData(RegistryHive.CurrentConfig)]
    public void Blocks_unmodeled_hive(RegistryHive hive)
        => Assert.False(TestData.Gate().Evaluate(TestData.RegKey(hive, "Anything\\Here")).Allowed);

    [Fact]
    public void Blocks_hku_without_a_sid()
        => Assert.False(TestData.Gate().Evaluate(TestData.RegKey(RegistryHive.Users, "SoftwareNoSid")).Allowed);

    // ---- Item 6: empty-string ValueName routes to the protected KEY-delete path, not the permissive value path ----

    [Theory]
    [InlineData("SOFTWARE\\Microsoft\\Windows")]            // protected key
    [InlineData("SOFTWARE\\Microsoft\\Windows\\CurrentVersion")]
    public void Empty_value_name_on_a_protected_subtree_key_is_blocked(string sub)
    {
        // StartupPlanner sets ValueName = entry.Name, which can be EMPTY ("(Default)" value). An empty
        // ValueName must route to the KEY-delete (protected) path — NOT the permissive value-delete path —
        // so it cannot delete a protected key's "(Default)" value under a protected subtree. The fix is
        // `!string.IsNullOrEmpty(r.ValueName)` at SafetyGate.cs (Item 6); with `is not null` an empty name
        // wrongly hit the value path and (for these keys, which are not on ProtectedValueKeys) ALLOWED.
        var v = TestData.Gate().Evaluate(TestData.RegValue(RegistryHive.LocalMachine, sub, ""));
        Assert.False(v.Allowed, "empty value-name delete on a protected key must be blocked: " + sub);
    }

    [Fact]
    public void A_normal_nonempty_value_delete_on_an_allowed_run_key_is_still_allowed()
    {
        // GUARDRAIL positive counter-test: the Item 6 hardening must NOT over-block a legitimate startup-entry
        // delete. A non-empty named value on the Run key (the real startup-disable path) stays allowed.
        var v = TestData.Gate().Evaluate(TestData.RegValue(
            RegistryHive.CurrentUser, "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", "SomeApp"));
        Assert.True(v.Allowed, v.Reason);
    }
}

using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

public class StartupPlannerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Hkcu_run_entry_becomes_an_allowed_value_delete_on_the_run_key()
    {
        var entry = new StartupEntry("Steam", @"C:\Steam\steam.exe -silent", StartupSource.HkcuRun, FolderPath: null);

        OperationPlan plan = StartupPlanner.BuildDisablePlan(entry, T0);

        var action = Assert.IsType<RegistryDeleteAction>(Assert.Single(plan.Actions));
        Assert.Equal(RegistryHive.CurrentUser, action.Hive);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", action.SubKeyPath);
        Assert.Equal("Steam", action.ValueName);
        Assert.Equal(RiskLevel.Medium, action.Risk);
        Assert.Equal(UndoCapability.Partial, action.Undo);

        // The gate must permit this value-delete (Run/RunOnce carve-out), otherwise the user could never disable it.
        Assert.True(TestData.Gate().Evaluate(action).Allowed);
    }

    [Fact]
    public void Hklm_runonce_entry_targets_the_machine_runonce_key()
    {
        var entry = new StartupEntry("Updater", @"C:\Vendor\update.exe", StartupSource.HklmRunOnce, FolderPath: null);

        OperationPlan plan = StartupPlanner.BuildDisablePlan(entry, T0);

        var action = Assert.IsType<RegistryDeleteAction>(Assert.Single(plan.Actions));
        Assert.Equal(RegistryHive.LocalMachine, action.Hive);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", action.SubKeyPath);
        Assert.Equal("Updater", action.ValueName);
    }

    [Fact]
    public void Startup_folder_entry_becomes_a_recycle_file_delete_on_the_lnk()
    {
        string lnk = @"C:\Users\alice\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\App.lnk";
        var entry = new StartupEntry("App", lnk, StartupSource.StartupFolder, FolderPath: lnk);

        OperationPlan plan = StartupPlanner.BuildDisablePlan(entry, T0);

        var action = Assert.IsType<FileDeleteAction>(Assert.Single(plan.Actions));
        Assert.Equal(lnk, action.Path);
        Assert.True(action.ToRecycleBin);
        Assert.Equal(RiskLevel.Low, action.Risk);
        Assert.Equal(UndoCapability.Full, action.Undo);
    }

    [Fact]
    public void It_never_emits_a_key_delete()
    {
        var entry = new StartupEntry("Vendor", @"C:\v\v.exe", StartupSource.HkcuRun, FolderPath: null);

        OperationPlan plan = StartupPlanner.BuildDisablePlan(entry, T0);

        // A key-delete would have ValueName == null; a value-delete (what we want) sets it.
        var action = Assert.IsType<RegistryDeleteAction>(Assert.Single(plan.Actions));
        Assert.NotNull(action.ValueName);
    }

    [Fact]
    public void A_folder_entry_without_a_path_throws_rather_than_emitting_a_bad_action()
    {
        var entry = new StartupEntry("Broken", "irrelevant", StartupSource.StartupFolder, FolderPath: null);
        Assert.Throws<ArgumentException>(() => StartupPlanner.BuildDisablePlan(entry, T0));
    }

    [Fact]
    public void Plan_is_in_the_clean_module()
    {
        var entry = new StartupEntry("X", "cmd", StartupSource.HkcuRun, FolderPath: null);
        OperationPlan plan = StartupPlanner.BuildDisablePlan(entry, T0);
        Assert.Equal("clean", plan.ModuleName);
    }
}

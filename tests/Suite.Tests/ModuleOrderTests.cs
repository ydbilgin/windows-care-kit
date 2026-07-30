using WindowsCareKit.App.Modules;
using WindowsCareKit.Module.Backup;
using WindowsCareKit.Module.Clean;
using WindowsCareKit.Module.Install;
using WindowsCareKit.Module.Migration;
using WindowsCareKit.Module.Restore;
using WindowsCareKit.Module.Uninstall;
using Xunit;

namespace WindowsCareKit.Tests;

public class ModuleOrderTests
{
    [Fact]
    public void Feature_bands_are_strictly_ascending_and_precede_settings()
    {
        Assert.True(ModuleOrder.Uninstall < ModuleOrder.Clean);
        Assert.True(ModuleOrder.Clean < ModuleOrder.Backup);
        Assert.True(ModuleOrder.Backup < ModuleOrder.Migration);
        Assert.True(ModuleOrder.Migration < ModuleOrder.Install);
        Assert.True(ModuleOrder.Install < ModuleOrder.Restore);
        Assert.True(ModuleOrder.Restore < ModuleOrder.Settings);
    }

    [Fact]
    public void Each_feature_module_reports_its_named_band()
    {
        Assert.Equal(ModuleOrder.Uninstall, new UninstallModule().Order);
        Assert.Equal(ModuleOrder.Clean, new CleanModule().Order);
        Assert.Equal(ModuleOrder.Backup, new BackupModule().Order);
        Assert.Equal(ModuleOrder.Migration, new MigrationModule().Order);
        Assert.Equal(ModuleOrder.Install, new InstallModule().Order);
        Assert.Equal(ModuleOrder.Restore, new RestoreModule().Order);
    }
}

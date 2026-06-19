using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

public class LeftoverScannerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Protected_candidates_are_skipped_not_planned()
    {
        var probe = new FakeLeftoverProbe();
        probe.Directories.Add(new LeftoverDirectory(@"C:\Program Files\SomeApp", "app folder"));
        probe.Directories.Add(new LeftoverDirectory(@"C:\Windows", "looks risky"));         // protected
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine, @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "vendor key"));
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows", RegistryView.Registry64, "system key")); // protected
        probe.Services.Add(new LeftoverService("SomeVendorSvc", "app service"));
        probe.Services.Add(new LeftoverService("RpcSs", "critical")); // protected

        var scanner = new LeftoverScanner(probe, TestData.Gate());
        var result = scanner.Scan(TestData.App(), T0);

        Assert.Equal(3, result.Plan.Actions.Count);   // the three safe ones
        Assert.Equal(3, result.Skipped.Count);          // the three protected ones
        Assert.All(result.Plan.Actions, a => Assert.True(TestData.Gate().Evaluate(a).Allowed));
    }

    [Fact]
    public void Risk_and_undo_are_classified_per_action_type()
    {
        var probe = new FakeLeftoverProbe();
        probe.Directories.Add(new LeftoverDirectory(@"C:\Program Files\SomeApp", "dir"));
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine, @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "key"));
        probe.Services.Add(new LeftoverService("SomeVendorSvc", "svc"));

        var result = new LeftoverScanner(probe, TestData.Gate()).Scan(TestData.App(), T0);

        var dir = Assert.IsType<FileDeleteAction>(result.Plan.Actions.First(a => a is FileDeleteAction));
        Assert.Equal(RiskLevel.Low, dir.Risk);
        Assert.Equal(UndoCapability.Full, dir.Undo);
        Assert.True(dir.ToRecycleBin);

        var reg = Assert.IsType<RegistryDeleteAction>(result.Plan.Actions.First(a => a is RegistryDeleteAction));
        Assert.Equal(RiskLevel.Medium, reg.Risk);
        Assert.Equal(UndoCapability.Partial, reg.Undo);

        var svc = Assert.IsType<ServiceDeleteAction>(result.Plan.Actions.First(a => a is ServiceDeleteAction));
        Assert.Equal(RiskLevel.High, svc.Risk);
        Assert.Equal(ServiceOperation.Stop, svc.Operation);
    }

    [Fact]
    public void Empty_probe_yields_empty_plan()
    {
        var result = new LeftoverScanner(new FakeLeftoverProbe(), TestData.Gate()).Scan(TestData.App(), T0);
        Assert.True(result.Plan.IsEmpty);
        Assert.Empty(result.Skipped);
    }
}

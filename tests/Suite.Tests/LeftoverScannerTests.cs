using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

public class LeftoverScannerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Gate_blocked_candidates_are_skipped_not_in_the_candidate_list()
    {
        // The SCANNER produces the full CANDIDATE list (gate-allowed, classified) plus a skipped (gate-blocked)
        // list, and a DELETABLE plan filtered to ProgramOwned only. This asserts the gate split: protected
        // candidates never enter the gate-allowed candidate set — they land in Skipped (PR-3 A).
        var probe = new FakeLeftoverProbe();
        probe.Directories.Add(new LeftoverDirectory(@"C:\Program Files\SomeApp", "app folder"));
        probe.Directories.Add(new LeftoverDirectory(@"C:\Windows", "looks risky"));         // protected
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine, @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "vendor key"));
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows", RegistryView.Registry64, "system key")); // protected
        probe.Services.Add(new LeftoverService("SomeVendorSvc", "app service"));
        probe.Services.Add(new LeftoverService("RpcSs", "critical")); // protected

        var scanner = new LeftoverScanner(probe, TestData.Gate());
        var result = scanner.Scan(TestData.App(), T0);

        // Three gate-allowed candidates (folder + vendor key + app service) appear in the candidate list, none
        // of them Protected; the three gate-protected ones are in Skipped (and classified Protected).
        var nonProtected = result.Candidates
            .Where(c => c.Classification != LeftoverClassification.Protected).ToList();
        Assert.Equal(3, nonProtected.Count);
        Assert.All(nonProtected, c => Assert.True(TestData.Gate().Evaluate(c.Action).Allowed));
        Assert.Equal(3, result.Skipped.Count);  // the three gate-protected ones
        Assert.Equal(3, result.Candidates.Count(c => c.Classification == LeftoverClassification.Protected));
    }

    [Fact]
    public void Deletable_plan_is_program_owned_only_shared_excluded()
    {
        // PR-3 A — the LIVE invariant: result.Plan (staged/executed) contains ONLY ProgramOwned actions. A
        // gate-allowed vendor PARENT (Shared) is in the candidate list but NOT in the deletable plan.
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.MachineWide64);
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "vendor leaf"));   // ProgramOwned
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\SomeVendor", RegistryView.Registry64, "vendor parent"));         // Shared
        probe.Directories.Add(new LeftoverDirectory(@"C:\Program Files\SomeApp", "app folder")); // Shared

        var result = new LeftoverScanner(probe, TestData.Gate()).Scan(app, T0);

        // Candidate list has all three; the deletable plan has only the ProgramOwned vendor leaf.
        Assert.Equal(3, result.Candidates.Count);
        var deletable = Assert.IsType<RegistryDeleteAction>(Assert.Single(result.Plan.Actions));
        Assert.Equal(@"SOFTWARE\SomeVendor\SomeApp", deletable.SubKeyPath);
    }

    [Fact]
    public void Gate_allowed_candidate_is_NOT_the_deletion_plan_classifier_filters_ownership()
    {
        // SUPERSEDES the old "vendor key lands in the plan" assertion (critic HIGH#1): being gate-allowed is no
        // longer sufficient to delete. A vendor PARENT key is gate-allowed (it appears in the candidate list),
        // yet the classifier marks it Shared, it is EXCLUDED from result.Plan, and the plan builder rejects it.
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor");
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\SomeVendor", RegistryView.Registry64, "vendor parent")); // gate-allowed, but Shared

        var scan = new LeftoverScanner(probe, TestData.Gate()).Scan(app, T0);
        Assert.True(TestData.Gate().Evaluate(scan.Candidates.Single().Action).Allowed); // gate allows it
        Assert.True(scan.Plan.IsEmpty); // but it is EXCLUDED from the deletable plan (Shared)

        var candidates = new LeftoverClassifier().Classify(app, scan);
        Assert.Equal(LeftoverClassification.Shared, candidates.Single().Classification);

        // Force-selecting it (bypassed checkbox) cannot build a deletion plan — it throws before authorize.
        Assert.Throws<LeftoverPlanBuildException>(() =>
            new LeftoverPlanBuilder().Build(app, new[] { candidates.Single() with { Selected = true } }, T0));
    }

    [Fact]
    public void Risk_and_undo_are_classified_per_action_type()
    {
        // Use ProgramOwned candidates so they land in result.Plan: the exact vendor leaf and a service whose
        // image path is under the install dir. (The folder stays Shared, so it is asserted via Candidates.)
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.MachineWide64, installLocation: @"C:\Program Files\SomeApp");
        var probe = new FakeLeftoverProbe();
        probe.Directories.Add(new LeftoverDirectory(@"C:\Program Files\SomeApp", "dir"));
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine, @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "key"));
        probe.Services.Add(new LeftoverService("SomeVendorSvc", "svc", @"C:\Program Files\SomeApp\svc.exe"));

        var result = new LeftoverScanner(probe, TestData.Gate()).Scan(app, T0);

        // The folder is Shared (display-only) — asserted via the candidate list, with its risk/undo intact.
        var dir = Assert.IsType<FileDeleteAction>(
            result.Candidates.Select(c => c.Action).First(a => a is FileDeleteAction));
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
    public void Empty_probe_yields_empty_plan_and_no_candidates()
    {
        var result = new LeftoverScanner(new FakeLeftoverProbe(), TestData.Gate()).Scan(TestData.App(), T0);
        Assert.True(result.Plan.IsEmpty);
        Assert.Empty(result.Skipped);
        Assert.Empty(result.Candidates);
    }
}

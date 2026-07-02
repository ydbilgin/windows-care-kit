using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Spec §6 (LEFTOVER 3-KADEME) — the safety-critical slice. Proves, non-vacuously, that:
///   - a vendor PARENT key / Installer\Products ref / HKCR ProgID is classified Shared, is not selectable,
///     and CANNOT enter the rebuilt deletion plan;
///   - building a plan that includes a Shared (or Protected) action THROWS before authorize;
///   - the genuine ProgramOwned leaf (exact Software\&lt;Publisher&gt;\&lt;DisplayName&gt;) is selectable and deletable.
/// </summary>
public class LeftoverClassifierTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static LeftoverScanResult Scan(FakeLeftoverProbe probe, InstalledApp app)
        => new LeftoverScanner(probe, TestData.Gate()).Scan(app, T0);

    private static LeftoverCandidate Candidate(IReadOnlyList<LeftoverCandidate> all, Func<PlannedAction, bool> match)
        => all.Single(c => match(c.Action));

    // ---- ProgramOwned: the three exact attributions ----

    [Fact]
    public void Own_uninstall_entry_is_program_owned_and_selectable()
    {
        var app = TestData.App(regKeyName: "SomeApp", source: InstalledAppSource.MachineWide64);
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SomeApp", RegistryView.Registry64, "uninstall entry"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is RegistryDeleteAction);

        Assert.Equal(LeftoverClassification.ProgramOwned, c.Classification);
        Assert.True(c.Selectable);
        Assert.False(c.Selected); // opt-in default
    }

    [Fact]
    public void Exact_vendor_leaf_is_program_owned_and_selectable()
    {
        // Machine-wide app ⇒ its own vendor leaf lives in HKLM (hive matches the app source — PR-3 C).
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.MachineWide64);
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "vendor leaf"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is RegistryDeleteAction);

        Assert.Equal(LeftoverClassification.ProgramOwned, c.Classification);
        Assert.True(c.Selectable);
    }

    [Fact]
    public void Service_under_install_dir_is_program_owned()
    {
        // PR-3 B: a service is ProgramOwned ONLY when its resolved ImagePath sits under the install directory.
        var app = TestData.App(installLocation: @"C:\Program Files\SomeApp");
        var probe = new FakeLeftoverProbe();
        probe.Services.Add(new LeftoverService("SomeVendorSvc", "service in install dir",
            @"C:\Program Files\SomeApp\svc.exe"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is ServiceDeleteAction);

        Assert.Equal(LeftoverClassification.ProgramOwned, c.Classification);
        Assert.True(c.Selectable);
    }

    [Fact]
    public void Foreign_service_outside_install_dir_is_shared_even_with_install_location()
    {
        // PR-3 B (the attribution hole): a non-empty install location is NOT enough. A foreign service whose
        // ImagePath lives OUTSIDE the install dir must be Shared (not selectable), even though InstallLocation
        // is set. This is the fail-without/pass-with case for the ImagePath segment check.
        var app = TestData.App(installLocation: @"C:\Program Files\SomeApp");
        var probe = new FakeLeftoverProbe();
        probe.Services.Add(new LeftoverService("ForeignSvc", "foreign service",
            @"C:\Program Files\OtherVendor\other.exe")); // outside the install dir

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is ServiceDeleteAction);

        Assert.Equal(LeftoverClassification.Shared, c.Classification);
        Assert.False(c.Selectable);
    }

    [Fact]
    public void Service_with_sibling_prefix_image_path_is_shared_not_program_owned()
    {
        // Segment-boundary guard: C:\Program Files\SomeAppX is NOT under C:\Program Files\SomeApp.
        var app = TestData.App(installLocation: @"C:\Program Files\SomeApp");
        var probe = new FakeLeftoverProbe();
        probe.Services.Add(new LeftoverService("SiblingSvc", "sibling-prefix dir",
            @"C:\Program Files\SomeAppX\svc.exe"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is ServiceDeleteAction);

        Assert.Equal(LeftoverClassification.Shared, c.Classification);
        Assert.False(c.Selectable);
    }

    [Fact]
    public void Service_with_unresolved_image_path_is_shared()
    {
        // ImagePath could not be resolved by the probe (null) ⇒ no attribution evidence ⇒ Shared (fail-safe).
        var app = TestData.App(installLocation: @"C:\Program Files\SomeApp");
        var probe = new FakeLeftoverProbe();
        probe.Services.Add(new LeftoverService("UnknownSvc", "no image path", ImagePath: null));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is ServiceDeleteAction);

        Assert.Equal(LeftoverClassification.Shared, c.Classification);
        Assert.False(c.Selectable);
    }

    // ---- Shared: the DEFAULT — vendor parent, Installer\Products, HKCR ProgID, App Paths, Run* ----

    [Fact]
    public void Vendor_parent_key_is_shared_not_program_owned()
    {
        // SOFTWARE\SomeVendor (the PARENT, no <DisplayName> leaf) is shared with the publisher's other apps.
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor");
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\SomeVendor", RegistryView.Registry64, "vendor parent"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is RegistryDeleteAction);

        Assert.Equal(LeftoverClassification.Shared, c.Classification);
        Assert.False(c.Selectable);
        Assert.False(c.Selected);
    }

    [Fact]
    public void Installer_products_ref_is_shared()
    {
        var app = TestData.App();
        var probe = new FakeLeftoverProbe();
        // A widened-probe source: Installer\Products is gate-allowed but never provably owned → Shared.
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\Classes\Installer\Products\A1B2C3", RegistryView.Registry64, "installer products ref"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is RegistryDeleteAction);

        Assert.Equal(LeftoverClassification.Shared, c.Classification);
        Assert.False(c.Selectable);
    }

    [Fact]
    public void Hkcr_progid_file_association_is_shared()
    {
        var app = TestData.App();
        var probe = new FakeLeftoverProbe();
        // HKCR ProgID — a file association is shared context (other apps may open the type) → Shared.
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.ClassesRoot,
            @"SomeApp.Document", RegistryView.Registry64, "HKCR ProgID"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is RegistryDeleteAction);

        Assert.Equal(LeftoverClassification.Shared, c.Classification);
        Assert.False(c.Selectable);
    }

    // ---- Hive negatives (PR-3 C): a vendor-SHAPED key in the wrong hive is NOT this app's leaf ----

    [Fact]
    public void Vendor_shaped_key_in_hkcr_is_shared_not_program_owned()
    {
        // The exact Software\<Publisher>\<DisplayName> SHAPE but in HKCR — not the app's own hive → Shared.
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.MachineWide64);
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.ClassesRoot,
            @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "vendor-shaped key in HKCR"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is RegistryDeleteAction);

        Assert.Equal(LeftoverClassification.Shared, c.Classification);
        Assert.False(c.Selectable);
    }

    [Fact]
    public void Vendor_shaped_key_in_hku_is_shared_not_program_owned()
    {
        // HKU\<current SID>\Software\<Publisher>\<DisplayName> — wrong hive for both per-user and machine-wide
        // but gate-allowed because it is the current user's HKU hive → Shared.
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.CurrentUser);
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.Users,
            TestData.CurrentUserSid + @"\SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "vendor-shaped key in HKU"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is RegistryDeleteAction);

        Assert.Equal(LeftoverClassification.Shared, c.Classification);
        Assert.False(c.Selectable);
    }

    [Fact]
    public void Vendor_leaf_in_hklm_for_a_per_user_app_is_shared()
    {
        // A per-user (HKCU) app: its OWN vendor leaf lives in HKCU. The identically-shaped key in HKLM belongs
        // to the machine-wide install of the same vendor's product and must NOT be attributed here → Shared.
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.CurrentUser);
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "HKLM leaf for a per-user app"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is RegistryDeleteAction);

        Assert.Equal(LeftoverClassification.Shared, c.Classification);
        Assert.False(c.Selectable);
    }

    [Fact]
    public void Vendor_leaf_in_hkcu_for_a_per_user_app_is_program_owned()
    {
        // The matching-hive case proves the hive check is not over-broad: per-user app + HKCU leaf → ProgramOwned.
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.CurrentUser);
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.CurrentUser,
            @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "HKCU leaf for a per-user app"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is RegistryDeleteAction);

        Assert.Equal(LeftoverClassification.ProgramOwned, c.Classification);
        Assert.True(c.Selectable);
    }

    // ---- Registry VALUE delete (ValueName != null) is never ProgramOwned (PR-3 F) ----

    [Fact]
    public void Vendor_leaf_value_delete_is_shared_not_program_owned()
    {
        // A VALUE delete under the exact vendor leaf is not the whole-entry attribution → Shared, not deletable.
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.MachineWide64);
        var scan = new LeftoverScanner(new FakeLeftoverProbe(), TestData.Gate()).Scan(app, T0);
        // Build a classified candidate directly from a value-delete action (the scanner only emits key deletes).
        var valueAction = TestData.RegValue(RegistryHive.LocalMachine, @"SOFTWARE\SomeVendor\SomeApp", "Setting");
        var candidates = new LeftoverClassifier().Classify(
            app, new PlannedAction[] { valueAction }, scan.Skipped);
        var c = candidates.Single();

        Assert.Equal(LeftoverClassification.Shared, c.Classification);
        Assert.False(c.Selectable);
    }

    [Fact]
    public void Own_uninstall_entry_value_delete_is_shared_not_program_owned()
    {
        var app = TestData.App(regKeyName: "SomeApp", source: InstalledAppSource.MachineWide64);
        var valueAction = TestData.RegValue(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SomeApp", "DisplayName");
        var candidates = new LeftoverClassifier().Classify(
            app, new PlannedAction[] { valueAction }, Array.Empty<SkippedAction>());
        var c = candidates.Single();

        Assert.Equal(LeftoverClassification.Shared, c.Classification);
        Assert.False(c.Selectable);
    }

    [Fact]
    public void Leftover_directory_is_shared_by_default()
    {
        // A folder named after the app is suggestive, not proof of sole ownership → Shared (display-only).
        var app = TestData.App();
        var probe = new FakeLeftoverProbe();
        probe.Directories.Add(new LeftoverDirectory(@"C:\Program Files\SomeApp", "app folder"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is FileDeleteAction);

        Assert.Equal(LeftoverClassification.Shared, c.Classification);
        Assert.False(c.Selectable);
    }

    [Fact]
    public void Service_without_install_dir_is_shared()
    {
        // No install location ⇒ cannot prove the service belongs to this app ⇒ Shared.
        var app = TestData.App(installLocation: null);
        var probe = new FakeLeftoverProbe();
        probe.Services.Add(new LeftoverService("SomeVendorSvc", "service, no install dir to correlate"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var c = Candidate(candidates, a => a is ServiceDeleteAction);

        Assert.Equal(LeftoverClassification.Shared, c.Classification);
        Assert.False(c.Selectable);
    }

    // ---- Protected: routed through the SAME gate (the skipped list), not duplicated here ----

    [Fact]
    public void Gate_blocked_action_is_protected_with_gate_reason()
    {
        var app = TestData.App();
        var probe = new FakeLeftoverProbe();
        probe.Directories.Add(new LeftoverDirectory(@"C:\Windows", "system dir"));            // gate blocks
        probe.Services.Add(new LeftoverService("RpcSs", "critical"));                          // gate blocks

        var scan = Scan(probe, app);
        var candidates = new LeftoverClassifier().Classify(app, scan);

        Assert.Equal(2, scan.Skipped.Count); // proof the gate is what marked these
        Assert.All(candidates, c =>
        {
            Assert.Equal(LeftoverClassification.Protected, c.Classification);
            Assert.False(c.Selectable);
        });
        // GateReason is the gate's own reason, not a fabricated one (no duplicated protected table).
        Assert.Contains(candidates, c => c.GateReason.Length > 0);
    }

    // ---- Plan-builder invariant: the real guard ----

    [Fact]
    public void Selected_shared_candidate_cannot_enter_plan_throws_before_authorize()
    {
        // FAIL-WITHOUT / PASS-WITH: a vendor PARENT key — gate-allowed, classified Shared. Force-select it
        // (simulating a bypassed UI checkbox) and try to build the plan: the invariant must THROW.
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor");
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\SomeVendor", RegistryView.Registry64, "vendor parent"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var shared = candidates.Single();
        Assert.Equal(LeftoverClassification.Shared, shared.Classification);

        // Confirm the gate ALONE does NOT block this (honesty: only the plan-builder invariant stops it).
        Assert.True(TestData.Gate().Evaluate(shared.Action).Allowed);

        var forced = new[] { shared with { Selected = true } };
        var ex = Assert.Throws<LeftoverPlanBuildException>(
            () => new LeftoverPlanBuilder().Build(app, forced, T0));
        Assert.Equal(LeftoverClassification.Shared, ex.Classification);
    }

    [Fact]
    public void Selected_protected_candidate_throws_before_authorize()
    {
        var app = TestData.App();
        var protectedCandidate = new LeftoverCandidate
        {
            Action = TestData.RegKey(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows"),
            Classification = LeftoverClassification.Protected,
            Selected = true,
            GateReason = "protected",
        };

        var ex = Assert.Throws<LeftoverPlanBuildException>(
            () => new LeftoverPlanBuilder().Build(app, new[] { protectedCandidate }, T0));
        Assert.Equal(LeftoverClassification.Protected, ex.Classification);
    }

    [Fact]
    public void Selected_program_owned_candidate_can_be_deleted()
    {
        // PASS-WITH: the genuine ProgramOwned leaf is selectable AND builds into a non-empty plan.
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.CurrentUser);
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.CurrentUser,
            @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "vendor leaf"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var owned = candidates.Single();
        Assert.Equal(LeftoverClassification.ProgramOwned, owned.Classification);

        var plan = new LeftoverPlanBuilder().Build(app, new[] { owned with { Selected = true } }, T0);

        Assert.Single(plan.Actions);
        Assert.IsType<RegistryDeleteAction>(plan.Actions[0]);
    }

    [Fact]
    public void Unselected_program_owned_does_not_enter_plan()
    {
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.CurrentUser);
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.CurrentUser,
            @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "vendor leaf"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var plan = new LeftoverPlanBuilder().Build(app, candidates, T0); // none selected (opt-in default)

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Rebuilt_plan_contains_only_program_owned_when_mixed_selection()
    {
        // A scan with one ProgramOwned (vendor leaf) + one Shared (vendor parent) + one Protected (system).
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.CurrentUser);
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.CurrentUser,
            @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "vendor leaf"));    // ProgramOwned
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\SomeVendor", RegistryView.Registry64, "vendor parent"));          // Shared
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows", RegistryView.Registry64, "system"));          // Protected

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));

        // Select EVERY selectable candidate (only ProgramOwned can be selected at all).
        var selected = candidates
            .Select(c => c.Selectable ? c with { Selected = true } : c)
            .ToArray();

        var plan = new LeftoverPlanBuilder().Build(app, selected, T0);

        Assert.Single(plan.Actions);
        var reg = Assert.IsType<RegistryDeleteAction>(plan.Actions[0]);
        Assert.Equal(@"SOFTWARE\SomeVendor\SomeApp", reg.SubKeyPath);
    }

    // ---- Builder edge hardening (PR-3 E): null rejection + dedupe by canonical signature ----

    [Fact]
    public void Build_rejects_a_null_candidate()
    {
        var app = TestData.App();
        var ex = Assert.Throws<ArgumentException>(
            () => new LeftoverPlanBuilder().Build(app, new LeftoverCandidate?[] { null }!, T0));
        Assert.Contains("candidate[0]", ex.Message);
    }

    [Fact]
    public void Build_dedupes_selected_program_owned_actions_by_target_signature()
    {
        // Two selected candidates that target the SAME registry key collapse to ONE action (stable hash).
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.CurrentUser);
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.CurrentUser,
            @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "vendor leaf"));

        var owned = new LeftoverClassifier().Classify(app, Scan(probe, app)).Single() with { Selected = true };
        // The same logical candidate selected twice (e.g. a duplicate row in the UI list).
        var duplicated = new[] { owned, owned with { } };

        var plan = new LeftoverPlanBuilder().Build(app, duplicated, T0);

        Assert.Single(plan.Actions); // de-duplicated, not two copies
    }

    // ---- Wiring to the REAL GatedExecutor path: the rebuilt plan hashes + re-validates cleanly ----

    [Fact]
    public void Rebuilt_program_owned_plan_passes_gate_validation_for_executor()
    {
        // The plan the builder produces must authorize through the SAME gate the GatedExecutor uses, and its
        // hash must be stable (the value ApproveAsync captures and the executor re-validates — spec §3).
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.CurrentUser);
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.CurrentUser,
            @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "vendor leaf"));

        var candidates = new LeftoverClassifier().Classify(app, Scan(probe, app));
        var plan = new LeftoverPlanBuilder().Build(app, new[] { candidates.Single() with { Selected = true } }, T0);

        var gate = TestData.Gate();
        Assert.True(gate.Validate(plan).AllAllowed);
        Assert.Equal(plan.ComputeHash(), plan.ComputeHash()); // deterministic
    }
}

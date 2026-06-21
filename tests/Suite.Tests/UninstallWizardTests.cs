using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// PR-4 — the 4-beat uninstall wizard view-model. Host-safe (no OS destruction): the probe + executor are
/// faked and nothing real is deleted. These tests pin the load-bearing wizard contracts:
///
/// <list type="bullet">
/// <item>the 3-tier <see cref="LeftoverNode.CanCheck"/> mapping (ProgramOwned checkable, Shared disabled,
/// Protected no-checkbox);</item>
/// <item>"Tümünü seç" scoped to ProgramOwned ONLY;</item>
/// <item>"Seçilenleri Sil" rebuilds the deletion plan via <see cref="LeftoverPlanBuilder"/> from the SELECTED
/// candidates and stages it through the existing ConfirmGate → executor pipeline;</item>
/// <item>a Shared row can never be checked nor staged;</item>
/// <item>ConfirmGate #1 (official uninstaller) and #2 (leftovers) are the only execution surfaces, and "İleri"
/// (next-without-deleting) never executes.</item>
/// </list>
/// </summary>
public class UninstallWizardTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static UninstallWizardViewModel BuildWizard(FakeLeftoverProbe probe, FakeExecutor executor)
    {
        var i18n = new I18n();
        i18n.Load("tr");
        return new UninstallWizardViewModel(i18n, TestData.Gate(), probe, executor, () => T0);
    }

    /// <summary>
    /// Approve a staged plan THROUGH the production confirm guard (Item 2) — never via a bare
    /// <c>Execute(null)</c> that skips <see cref="ConfirmGateViewModel.CanApprove"/>. For the Irreversible tier
    /// (the official-uninstaller command, Undo=None) it asserts Approve is LOCKED, types the localized confirm
    /// word, and asserts it UNLOCKS before executing. For lower tiers (the leftover registry deletes are
    /// Medium — they carry a .reg backup) it asserts Approve is already enabled, so the approval still goes
    /// through the real <c>CanApprove</c> gate, then executes.
    /// </summary>
    private static void ApproveThroughGuard(UninstallWizardViewModel wizard)
    {
        if (wizard.Gate.Tier == ConfirmTier.Irreversible)
        {
            Assert.False(wizard.Gate.ApproveCommand.CanExecute(null)); // locked until the confirm word is typed
            wizard.Gate.TypedConfirm = wizard.Gate.ConfirmWord;
        }
        Assert.True(wizard.Gate.ApproveCommand.CanExecute(null));      // CanApprove gate passed (per tier)
        wizard.Gate.ApproveCommand.Execute(null);
    }

    // Generous ceiling (flaky-fix 2026-06-21): the happy path exits the instant until() is true (~ms),
    // so a large cap never slows passing tests; it only prevents false "did not complete in time"
    // failures when the async ViewModel settle runs slow under CI/Release load (was 2000ms → flaked).
    private static async Task PumpAsync(Func<bool> until, int timeoutMs = 30_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10);
        Assert.True(until(), "operation did not complete in time");
    }

    /// <summary>A probe emitting one ProgramOwned vendor leaf + one Shared vendor parent + one Protected key.</summary>
    private static FakeLeftoverProbe ProbeWithAllThreeTiers()
    {
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "vendor leaf (ProgramOwned)"));
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\SomeVendor", RegistryView.Registry64, "vendor parent (Shared)"));
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows", RegistryView.Registry64, "system key (Protected)"));
        return probe;
    }

    private static InstalledApp MachineWideApp() => TestData.App(
        displayName: "SomeApp", publisher: "SomeVendor", source: InstalledAppSource.MachineWide64,
        uninstall: "\"C:\\Program Files\\SomeApp\\uninst.exe\" /S",
        // Phase 2: an elevated (machine-wide) uninstaller is anchored to the app's install dir; the exe sits under it.
        installLocation: @"C:\Program Files\SomeApp");

    /// <summary>Open the wizard, run the official uninstaller (ConfirmGate #1), then scan — landing on beat 3.</summary>
    private static async Task<UninstallWizardViewModel> OpenScannedAsync(FakeLeftoverProbe probe, FakeExecutor executor)
    {
        var wizard = BuildWizard(probe, executor);
        wizard.Open(MachineWideApp());
        // Skip the official run and go straight to scan to keep the scenario focused on the leftovers beat.
        wizard.ScanCommand.Execute(null);
        await PumpAsync(() => wizard.Beat == UninstallWizardViewModel.WizardBeat.Leftovers);
        return wizard;
    }

    // ---- 3-tier CanCheck mapping ----

    [Fact]
    public async Task Three_tier_nodes_map_canCheck_per_classification()
    {
        var wizard = await OpenScannedAsync(ProbeWithAllThreeTiers(), new FakeExecutor());

        LeftoverNode owned = wizard.RegistryNodes.Single(n => n.IsProgramOwned);
        LeftoverNode shared = wizard.RegistryNodes.Single(n => n.IsShared);
        LeftoverNode protectedNode = wizard.RegistryNodes.Single(n => n.IsProtected);

        // ProgramOwned: checkable, has a checkbox, DEFAULT UNCHECKED.
        Assert.True(owned.CanCheck);
        Assert.True(owned.HasCheckbox);
        Assert.False(owned.IsChecked);

        // Shared: NOT checkable, but still shown with a (disabled) checkbox for context.
        Assert.False(shared.CanCheck);
        Assert.True(shared.HasCheckbox);

        // Protected: NOT checkable and NO checkbox at all (teal + shield).
        Assert.False(protectedNode.CanCheck);
        Assert.False(protectedNode.HasCheckbox);
    }

    [Fact]
    public async Task Shared_node_cannot_be_checked_even_if_set()
    {
        var wizard = await OpenScannedAsync(ProbeWithAllThreeTiers(), new FakeExecutor());
        LeftoverNode shared = wizard.RegistryNodes.Single(n => n.IsShared);

        shared.IsChecked = true; // a bypassed checkbox attempt

        Assert.False(shared.IsChecked); // the setter is a no-op for a non-checkable node
        Assert.False(wizard.HasCheckedLeftovers);
    }

    [Fact]
    public async Task Protected_node_cannot_be_checked_even_if_set()
    {
        var wizard = await OpenScannedAsync(ProbeWithAllThreeTiers(), new FakeExecutor());
        LeftoverNode prot = wizard.RegistryNodes.Single(n => n.IsProtected);

        prot.IsChecked = true;

        Assert.False(prot.IsChecked);
        Assert.False(wizard.HasCheckedLeftovers);
    }

    // ---- "Tümünü seç" scoping ----

    [Fact]
    public async Task SelectAll_checks_only_program_owned_rows()
    {
        var wizard = await OpenScannedAsync(ProbeWithAllThreeTiers(), new FakeExecutor());

        wizard.SelectAllOwnedCommand.Execute(null);

        Assert.True(wizard.RegistryNodes.Single(n => n.IsProgramOwned).IsChecked);
        Assert.False(wizard.RegistryNodes.Single(n => n.IsShared).IsChecked);
        Assert.False(wizard.RegistryNodes.Single(n => n.IsProtected).IsChecked);
        Assert.Equal(1, wizard.CheckedCount);   // exactly the one ProgramOwned leaf
        Assert.Equal(1, wizard.OwnedCount);
    }

    [Fact]
    public async Task SelectAll_toggles_off_when_all_owned_already_checked()
    {
        var wizard = await OpenScannedAsync(ProbeWithAllThreeTiers(), new FakeExecutor());

        wizard.SelectAllOwnedCommand.Execute(null); // select
        Assert.Equal(1, wizard.CheckedCount);

        wizard.SelectAllOwnedCommand.Execute(null); // toggle off
        Assert.Equal(0, wizard.CheckedCount);
        Assert.False(wizard.HasCheckedLeftovers);
    }

    // ---- "Seçilenleri Sil" builds via LeftoverPlanBuilder ----

    [Fact]
    public async Task DeleteSelected_stages_a_plan_built_from_the_selected_program_owned_candidate()
    {
        var executor = new FakeExecutor();
        var wizard = await OpenScannedAsync(ProbeWithAllThreeTiers(), executor);

        wizard.RegistryNodes.Single(n => n.IsProgramOwned).IsChecked = true;
        wizard.DeleteSelectedCommand.Execute(null);

        // Staging only OPENS the gate — nothing has executed.
        Assert.True(wizard.Gate.IsOpen);
        Assert.Equal(0, executor.CallCount);

        // The staged plan (the gate's rows) is exactly the one ProgramOwned vendor leaf — the same plan
        // LeftoverPlanBuilder would build from that selection.
        var manualPlan = new LeftoverPlanBuilder().Build(MachineWideApp(),
            wizard.RegistryNodes.Concat(wizard.FileNodes).Select(n => n.ToCandidate()).ToList(), T0);
        var staged = Assert.Single(manualPlan.Actions);
        var reg = Assert.IsType<RegistryDeleteAction>(staged);
        Assert.Equal(@"SOFTWARE\SomeVendor\SomeApp", reg.SubKeyPath);
        Assert.Single(wizard.Gate.Rows); // the gate shows exactly that one row
    }

    [Fact]
    public async Task Approving_leftovers_runs_the_exact_staged_plan_hash_then_lands_on_result()
    {
        var executor = new FakeExecutor();
        var wizard = await OpenScannedAsync(ProbeWithAllThreeTiers(), executor);

        wizard.RegistryNodes.Single(n => n.IsProgramOwned).IsChecked = true;
        wizard.DeleteSelectedCommand.Execute(null);
        ApproveThroughGuard(wizard);

        await PumpAsync(() => wizard.IsResultBeat && wizard.HasResult);

        Assert.Equal(1, executor.CallCount);
        Assert.NotNull(executor.LastPlan);
        // No drift between the staged plan and the hash the VM authorized.
        Assert.Equal(executor.LastPlan!.ComputeHash(), executor.LastHash);
        // The plan the executor ran contains ONLY the ProgramOwned vendor leaf.
        var reg = Assert.IsType<RegistryDeleteAction>(Assert.Single(executor.LastPlan!.Actions));
        Assert.Equal(@"SOFTWARE\SomeVendor\SomeApp", reg.SubKeyPath);
    }

    [Fact]
    public async Task DeleteSelected_with_no_selection_does_not_open_the_gate_or_execute()
    {
        var executor = new FakeExecutor();
        var wizard = await OpenScannedAsync(ProbeWithAllThreeTiers(), executor);

        // No node checked → an empty plan → nothing staged.
        wizard.DeleteSelectedCommand.Execute(null);

        Assert.False(wizard.Gate.IsOpen);
        Assert.Equal(0, executor.CallCount);
    }

    // ---- "İleri" never deletes ----

    [Fact]
    public async Task Next_without_deleting_advances_to_result_without_executing()
    {
        var executor = new FakeExecutor();
        var wizard = await OpenScannedAsync(ProbeWithAllThreeTiers(), executor);

        // Even with a ProgramOwned row checked, "İleri" must not delete it.
        wizard.RegistryNodes.Single(n => n.IsProgramOwned).IsChecked = true;
        wizard.NextWithoutDeletingCommand.Execute(null);

        Assert.True(wizard.IsResultBeat);
        Assert.Equal(0, executor.CallCount); // nothing ran
        Assert.False(wizard.Gate.IsOpen);
    }

    // ---- ConfirmGate #1: official uninstaller ----

    [Fact]
    public void RunOfficial_stages_a_command_plan_through_the_gate_without_executing()
    {
        var executor = new FakeExecutor();
        var wizard = BuildWizard(new FakeLeftoverProbe(), executor);
        wizard.Open(MachineWideApp());

        Assert.True(wizard.CanRunOfficial);
        wizard.RunOfficialCommand.Execute(null);

        Assert.True(wizard.Gate.IsOpen);     // ConfirmGate #1 opened
        Assert.Equal(0, executor.CallCount); // staging never executes
        Assert.Single(wizard.Gate.Rows);     // the single official-uninstaller command row
    }

    [Fact]
    public async Task Approving_official_runs_the_command_plan_then_advances_to_scan()
    {
        var executor = new FakeExecutor();
        var wizard = BuildWizard(new FakeLeftoverProbe(), executor);
        wizard.Open(MachineWideApp());

        wizard.RunOfficialCommand.Execute(null);
        ApproveThroughGuard(wizard);

        await PumpAsync(() => wizard.IsScanBeat);

        Assert.Equal(1, executor.CallCount);
        var cmd = Assert.IsType<CommandAction>(Assert.Single(executor.LastPlan!.Actions));
        Assert.Contains("uninst.exe", cmd.FileName);
        Assert.True(wizard.IsScanBeat); // official ran → advanced to the scan beat
    }

    [Fact]
    public void Broken_uninstaller_app_cannot_run_official_and_offers_a_skip()
    {
        var executor = new FakeExecutor();
        var app = TestData.App(displayName: "Broken", uninstall: null, quietUninstall: null);
        var i18n = new I18n();
        i18n.Load("tr");
        var wizard = new UninstallWizardViewModel(i18n, TestData.Gate(), new FakeLeftoverProbe(), executor, () => T0);
        wizard.Open(app);

        Assert.False(wizard.CanRunOfficial);
        Assert.True(wizard.OfficialUnavailable);

        // The "run official" command is a no-op when there's no uninstaller — nothing is staged.
        wizard.RunOfficialCommand.Execute(null);
        Assert.False(wizard.Gate.IsOpen);
        Assert.Equal(0, executor.CallCount);
    }

    // ---- Command-policy Phase 2 (Fix 8): manual fallback for an elevated uninstaller that cannot anchor ----

    [Fact]
    public void Elevated_uninstaller_with_an_unanchorable_install_location_falls_back_to_manual()
    {
        // A machine-wide (elevated) app whose UninstallString points OUTSIDE a missing/stale InstallLocation can
        // no longer auto-run (Phase 2 anchor) → OfficialUninstallerPlanner.Build returns null → the wizard surfaces
        // the SAME manual fallback as a broken uninstaller: CanRunOfficial is false, OfficialUnavailable is true,
        // and "Taramaya geç →" (CanSkipToScan) is offered. This is the host-safe, ViewModel-level Fix 8 proof.
        var executor = new FakeExecutor();
        var app = TestData.App(
            displayName: "MachineWideNoLoc", source: InstalledAppSource.MachineWide64,
            uninstall: "\"C:\\Program Files\\SomeApp\\uninst.exe\" /S",
            installLocation: null); // stale/missing → the elevated uninstaller cannot anchor
        var i18n = new I18n();
        i18n.Load("tr");
        var wizard = new UninstallWizardViewModel(i18n, TestData.Gate(), new FakeLeftoverProbe(), executor, () => T0);
        wizard.Open(app);

        Assert.False(wizard.CanRunOfficial);     // no auto official run
        Assert.True(wizard.OfficialUnavailable);  // surfaces the honest "manual" note
        Assert.True(wizard.CanSkipToScan);        // the manual path ("go to scan") is offered

        // Pressing "run official" stages nothing and runs nothing (no broken/no-op auto action).
        wizard.RunOfficialCommand.Execute(null);
        Assert.False(wizard.Gate.IsOpen);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public void Elevated_uninstaller_anchored_under_its_install_location_can_still_run_official()
    {
        // Paired positive: with a MATCHING InstallLocation the elevated uninstaller still auto-runs (no regression
        // for the common case). The exe sits under the install dir, so the Phase-2 anchor is satisfied.
        var executor = new FakeExecutor();
        var app = TestData.App(
            displayName: "MachineWideWithLoc", source: InstalledAppSource.MachineWide64,
            uninstall: "\"C:\\Program Files\\SomeApp\\uninst.exe\" /S",
            installLocation: @"C:\Program Files\SomeApp");
        var i18n = new I18n();
        i18n.Load("tr");
        var wizard = new UninstallWizardViewModel(i18n, TestData.Gate(), new FakeLeftoverProbe(), executor, () => T0);
        wizard.Open(app);

        Assert.True(wizard.CanRunOfficial);
        Assert.False(wizard.OfficialUnavailable);
    }

    // ---- Real GatedExecutor integration: Shared/Protected never reach the adapter ----

    [Fact]
    public async Task Shared_and_protected_never_reach_the_recording_adapter_via_the_wizard()
    {
        // The wizard → LeftoverPlanBuilder → stage → REAL GatedExecutor path. Select the ProgramOwned leaf,
        // delete, approve, and assert the recording registry adapter ran EXACTLY ONCE — for the leaf only.
        using var fx = new WindowsCareKit.Tests.Execution.ExecutorFixture(TestData.Gate());
        var i18n = new I18n();
        i18n.Load("tr");
        var wizard = new UninstallWizardViewModel(i18n, TestData.Gate(), ProbeWithAllThreeTiers(), fx.Executor, () => T0);
        wizard.Open(MachineWideApp());
        wizard.ScanCommand.Execute(null);
        await PumpAsync(() => wizard.Beat == UninstallWizardViewModel.WizardBeat.Leftovers);

        // Select ALL (which only checks the one ProgramOwned leaf) and delete.
        wizard.SelectAllOwnedCommand.Execute(null);
        wizard.DeleteSelectedCommand.Execute(null);
        ApproveThroughGuard(wizard);
        await PumpAsync(() => wizard.IsResultBeat && wizard.HasResult);

        Assert.Single(fx.Adapters.Calls);
        var dispatched = Assert.IsType<RegistryDeleteAction>(Assert.Single(fx.Adapters.Dispatched));
        Assert.Equal(@"SOFTWARE\SomeVendor\SomeApp", dispatched.SubKeyPath);
        Assert.DoesNotContain(fx.Adapters.Dispatched.OfType<RegistryDeleteAction>(),
            r => r.SubKeyPath.Equals(@"SOFTWARE\SomeVendor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fx.Adapters.Dispatched.OfType<RegistryDeleteAction>(),
            r => r.SubKeyPath.Equals(@"SOFTWARE\Microsoft\Windows", StringComparison.OrdinalIgnoreCase));
    }

    // ---- defense-in-depth: the builder guard, not just the UI CanCheck filter ----

    [Fact]
    public async Task Force_injecting_a_selected_shared_candidate_throws_in_the_builder_and_reaches_no_adapter()
    {
        // FORCE-INJECT proof (fix #6). The existing wizard integration test goes through SelectAllOwnedCommand,
        // which already filters by CanCheck at the UI level — so it would pass even if the builder guard were
        // removed. This test BYPASSES the UI CanCheck: it crafts a SELECTED candidate of a NON-ProgramOwned
        // (Shared) classification and (a) asserts LeftoverPlanBuilder.Build itself THROWS
        // LeftoverPlanBuildException — isolating the builder guard as defense-in-depth — and (b) drives the
        // wizard's StageLeftovers with that same forced candidate and asserts NOTHING reaches the recording
        // adapter, the gate never opens, and the failure surfaces loudly (fix #8: fail-loud, not crash).
        using var fx = new WindowsCareKit.Tests.Execution.ExecutorFixture(TestData.Gate());
        var i18n = new I18n();
        i18n.Load("tr");
        var wizard = new UninstallWizardViewModel(i18n, TestData.Gate(), ProbeWithAllThreeTiers(), fx.Executor, () => T0);
        wizard.Open(MachineWideApp());
        wizard.ScanCommand.Execute(null);
        await PumpAsync(() => wizard.Beat == UninstallWizardViewModel.WizardBeat.Leftovers);

        // Craft a SELECTED Shared candidate — exactly what the UI CanCheck filter would never allow.
        var forcedShared = new LeftoverCandidate
        {
            Action = new RegistryDeleteAction
            {
                Hive = RegistryHive.LocalMachine,
                SubKeyPath = @"SOFTWARE\SomeVendor",
                View = RegistryView.Registry64,
                Description = "vendor parent (Shared)",
                Reason = "shared",
            },
            Classification = LeftoverClassification.Shared,
            Selected = true, // the bypass: a Shared candidate marked selected
            GateReason = "shared",
        };

        // (a) The builder guard itself throws — proving the barrier is the builder, not the UI filter.
        Assert.Throws<LeftoverPlanBuildException>(() =>
            new LeftoverPlanBuilder().Build(MachineWideApp(), new[] { forcedShared }, T0));

        // (b) Drive the wizard's StageLeftovers with the forced candidate: it must fail LOUD, not crash, and
        // must stage/execute nothing.
        wizard.StageLeftovers(new[] { forcedShared });

        Assert.True(wizard.HasBuildError);          // surfaced loudly (fix #8)
        Assert.NotEqual(string.Empty, wizard.BuildError);
        Assert.False(wizard.Gate.IsOpen);           // no confirm gate opened
        Assert.Empty(fx.Adapters.Calls);            // the recording adapter was NEVER called
        Assert.False(wizard.IsResultBeat);          // still on the leftovers beat — nothing ran
    }

    // ---- file vs registry split + result protected count ----

    [Fact]
    public async Task File_leftovers_land_in_the_files_tab_and_registry_in_the_tree()
    {
        var probe = ProbeWithAllThreeTiers();
        probe.Directories.Add(new LeftoverDirectory(@"C:\Users\alice\AppData\Local\SomeApp", "app folder"));
        var wizard = await OpenScannedAsync(probe, new FakeExecutor());

        // The folder candidate is a FileDeleteAction → Files tab; the registry keys → registry tree.
        Assert.Contains(wizard.FileNodes, n => n.TargetText.Contains("SomeApp"));
        Assert.All(wizard.RegistryNodes, n => Assert.DoesNotContain("AppData", n.TargetText));
    }

    [Fact]
    public async Task Result_summary_counts_protected_items_as_skipped()
    {
        var executor = new FakeExecutor();
        var wizard = await OpenScannedAsync(ProbeWithAllThreeTiers(), executor);

        wizard.RegistryNodes.Single(n => n.IsProgramOwned).IsChecked = true;
        wizard.DeleteSelectedCommand.Execute(null);
        ApproveThroughGuard(wizard);
        await PumpAsync(() => wizard.IsResultBeat && wizard.HasResult);

        // One ProgramOwned removed + one Protected (the system key) skipped & surfaced as a teal line.
        Assert.Single(wizard.ProtectedLines);
        // Summary is "{0} tamam · {1} atlandı · {2} başarısız" — 1 done, 1 skipped (the protected key), 0 failed.
        Assert.StartsWith("1 tamam", wizard.ResultSummary);
        Assert.Contains("1 atlandı", wizard.ResultSummary);
        Assert.Contains("0 başarısız", wizard.ResultSummary);
    }

    // ---- fakes ----

    private sealed class FakeExecutor : IExecutor
    {
        public int CallCount { get; private set; }
        public OperationPlan? LastPlan { get; private set; }
        public string? LastHash { get; private set; }

        public ExecutionOutcome Execute(OperationPlan plan, string approvedPlanHash)
        {
            CallCount++;
            LastPlan = plan;
            LastHash = approvedPlanHash;
            return new ExecutionOutcome(true, "faked");
        }
    }
}

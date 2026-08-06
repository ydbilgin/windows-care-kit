using System.Text.Json;
using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Module.Uninstall.ViewModels;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// M3 — the Uninstall screen's ONE destructive door, asserted at the view-model seam.
/// <para>
/// The 4-beat wizard's two approval doors are gone: a removal is now composed once (restore point, vendor
/// uninstaller, leftovers), shown once, vetoed once, and executed as exactly the subset the user approved.
/// These tests carry the invariants the retired wizard tests protected — the ProgramOwned-only barrier, the
/// fail-loud build guard, restore-point co-staging and its tier exemption — onto the flow that replaced it.
/// </para>
/// </summary>
public class UninstallRemovalTests
{
    private const string UninstallString = "\"C:\\Program Files\\SomeApp\\uninst.exe\" /S";
    private const string InstallLocation = @"C:\Program Files\SomeApp";

    // ---- A-M3-5: the composed subset is what is hashed and what runs ----

    /// <summary>
    /// A-M3-5. Approving with every leftover left unchecked runs the restore point and the vendor step and
    /// NOTHING else, and the hash handed to the executor is that subset's own hash. Approving with the
    /// leftover checked runs three actions. Both halves matter: the first proves the veto subtracts, the
    /// second proves it is a veto rather than a permanently dropped action.
    /// </summary>
    [Fact]
    public async Task Approving_runs_exactly_the_subset_the_user_kept_and_the_hash_matches_it()
    {
        (UninstallViewModel vm, RecordingExecutor executor) = await StagedRemovalAsync();

        Assert.True(vm.Gate.IsOpen);
        Assert.Equal(1, vm.Gate.OptionalTotalCount);
        Assert.Equal(0, vm.Gate.OptionalIncludedCount);

        Approve(vm);

        OperationPlan ran = Assert.Single(executor.Plans);
        Assert.Equal(2, ran.Actions.Count);
        Assert.IsType<CreateRestorePointAction>(ran.Actions[0]);
        Assert.IsType<CommandAction>(ran.Actions[1]);
        Assert.DoesNotContain(ran.Actions, action => action is RegistryDeleteAction);

        // The TOCTOU contract: the approved hash IS the executed plan's hash (SPEC §1.1).
        Assert.Equal(ran.ComputeHash(), Assert.Single(executor.Hashes));
    }

    [Fact]
    public async Task A_checked_leftover_joins_the_approved_plan_in_the_source_plans_order()
    {
        (UninstallViewModel vm, RecordingExecutor executor) = await StagedRemovalAsync();

        PlanRow leftover = Assert.Single(vm.Gate.Rows, row => row.IsVetoable);
        leftover.IsIncluded = true;
        Assert.Equal(1, vm.Gate.OptionalIncludedCount);

        Approve(vm);

        OperationPlan ran = Assert.Single(executor.Plans);
        Assert.Equal(3, ran.Actions.Count);
        // Order is the SOURCE plan's, never the view's: the protective step first, the vendor step next.
        Assert.IsType<CreateRestorePointAction>(ran.Actions[0]);
        Assert.IsType<CommandAction>(ran.Actions[1]);
        Assert.IsType<RegistryDeleteAction>(ran.Actions[2]);
        Assert.Equal(ran.ComputeHash(), Assert.Single(executor.Hashes));
    }

    /// <summary>
    /// A-M3-5 / SPEC §2.3 carry-into-M3. The gate renders required rows and <c>WON'T RUN</c> rows in the SAME
    /// list, so the distinction has to be load-bearing exactly where the blast radius is highest: a gate
    /// containing a skipped row composes a plan that excludes it.
    /// </summary>
    [Fact]
    public async Task A_gate_holding_a_will_not_run_row_composes_a_plan_that_excludes_it()
    {
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(OwnedKey());
        // A vendor PARENT key: not solely owned by this app, so the classifier calls it Shared.
        probe.RegistryKeys.Add(new LeftoverRegistryKey(
            RegistryHive.LocalMachine, @"SOFTWARE\SomeVendor", RegistryView.Registry64, "vendor parent"));

        (UninstallViewModel vm, RecordingExecutor executor) = await StagedRemovalAsync(probe);

        PlanRow blocked = Assert.Single(vm.Gate.Rows, row => row.IsSkipped);
        Assert.Equal(RowSelection.Blocked, blocked.Selection);
        Assert.False(string.IsNullOrWhiteSpace(blocked.Detail));   // it states WHY, inline

        // Check everything that CAN be checked; the blocked row still cannot be resurrected.
        foreach (PlanRow row in vm.Gate.Rows.Where(row => row.IsVetoable))
            row.IsIncluded = true;

        Approve(vm);

        OperationPlan ran = Assert.Single(executor.Plans);
        Assert.DoesNotContain(ran.Actions, action => ReferenceEquals(action, blocked.Action));
        Assert.Equal(3, ran.Actions.Count); // restore point + vendor + the one OWNED leftover
    }

    // ---- A-M3-2: required means required ----

    /// <summary>
    /// A-M3-2. The restore point and the vendor uninstall are <see cref="RowSelection.Required"/>: they carry
    /// no veto, and trying to set one throws rather than recording a choice nothing downstream could honour.
    /// The rendered half of this — a lock badge and no checkbox ELEMENT — is asserted in
    /// <c>UninstallScreenTests</c>.
    /// </summary>
    [Fact]
    public async Task The_restore_point_and_the_vendor_step_cannot_be_unchecked()
    {
        (UninstallViewModel vm, _) = await StagedRemovalAsync();

        PlanRow[] required = vm.Gate.Rows
            .Where(row => row.Selection == RowSelection.Required)
            .ToArray();

        Assert.Equal(2, required.Length);
        Assert.Contains(required, row => row.Action is CreateRestorePointAction);
        Assert.Contains(required, row => row.Action is CommandAction);

        foreach (PlanRow row in required)
        {
            Assert.False(row.IsVetoable);
            Assert.Throws<InvalidOperationException>(() => row.IsIncluded = true);
        }
    }

    /// <summary>The restore point is co-staged as a NEIGHBOUR of the destructive step, and it never raises the
    /// bar: the tier is Irreversible because the vendor uninstaller has no undo, not because of the snapshot
    /// (which is protective and tier-exempt).</summary>
    [Fact]
    public async Task The_restore_point_is_prepended_and_does_not_drive_the_tier()
    {
        (UninstallViewModel vm, _) = await StagedRemovalAsync();

        Assert.Equal(ConfirmTier.Irreversible, vm.Gate.Tier);
        Assert.IsType<CreateRestorePointAction>(vm.Gate.Rows[0].Action);

        var restorePointOnly = new OperationPlan("rp", "uninstall",
            new PlannedAction[] { (PlannedAction)vm.Gate.Rows[0].Action! }, DateTime.UtcNow);
        Assert.Equal(ConfirmTier.Reversible, ConfirmGateViewModel.TierFor(restorePointOnly));
    }

    [Fact]
    public async Task No_restore_point_is_staged_when_the_capability_is_absent()
    {
        (UninstallViewModel vm, _) = await StagedRemovalAsync(restorePointAvailable: false);

        Assert.False(vm.RestorePointAvailable);
        Assert.DoesNotContain(vm.Gate.Rows, row => row.Action is CreateRestorePointAction);
        Assert.IsType<CommandAction>(vm.Gate.Rows[0].Action);
    }

    [Fact]
    public async Task Turning_the_restore_point_off_removes_it_from_the_plan_the_gate_shows()
    {
        (UninstallViewModel vm, _) = await ScannedAsync();
        Assert.True(vm.RestorePointEnabled);

        vm.RestorePointEnabled = false;
        vm.UninstallSelectedCommand.Execute(null);

        Assert.DoesNotContain(vm.Gate.Rows, row => row.Action is CreateRestorePointAction);
    }

    // ---- Nothing runs without approval ----

    [Fact]
    public async Task Staging_opens_the_gate_and_executes_nothing()
    {
        (UninstallViewModel vm, RecordingExecutor executor) = await StagedRemovalAsync();

        Assert.True(vm.Gate.IsOpen);
        Assert.True(vm.RequiresConfirmation);
        Assert.Empty(executor.Plans);
        Assert.False(vm.Gate.CanApprove); // the irreversible tier still wants the typed word
    }

    [Fact]
    public async Task Cancelling_the_gate_leaves_nothing_staged_and_nothing_run()
    {
        (UninstallViewModel vm, RecordingExecutor executor) = await StagedRemovalAsync();

        vm.Gate.CancelCommand.Execute(null);

        Assert.False(vm.Gate.IsOpen);
        Assert.False(vm.RequiresConfirmation);
        Assert.Empty(executor.Plans);
    }

    /// <summary>An app with no usable uninstaller can still have its leftovers removed — the manual path the
    /// wizard reached through "Go to scan" is now simply the same door with no vendor row (decision §2.5-13:
    /// no control loses reachability).</summary>
    [Fact]
    public async Task A_broken_uninstaller_app_still_reaches_its_leftovers_through_the_same_door()
    {
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(OwnedKey());
        (UninstallViewModel vm, RecordingExecutor executor) =
            await ScannedAsync(probe, uninstall: null);

        Assert.True(vm.VendorCommandUnavailable);
        Assert.Empty(vm.VendorCommandLine);
        Assert.True(vm.CanUninstallSelected);

        vm.UninstallSelectedCommand.Execute(null);
        Assert.DoesNotContain(vm.Gate.Rows, row => row.Action is CommandAction);
        // No vendor step means no restore point either: the snapshot rides with the step that has no undo.
        Assert.DoesNotContain(vm.Gate.Rows, row => row.Action is CreateRestorePointAction);

        Assert.Single(vm.Gate.Rows, row => row.IsVetoable);
        vm.Gate.Rows.Single(row => row.IsVetoable).IsIncluded = true;
        Approve(vm);

        Assert.IsType<RegistryDeleteAction>(Assert.Single(Assert.Single(executor.Plans).Actions));
    }

    /// <summary>Nothing to run at all is stated by DISABLING the door, not by opening it on an empty plan.</summary>
    [Fact]
    public async Task An_app_with_no_uninstaller_and_no_leftovers_cannot_open_the_door()
    {
        (UninstallViewModel vm, RecordingExecutor executor) =
            await ScannedAsync(new FakeLeftoverProbe(), uninstall: null);

        Assert.False(vm.CanUninstallSelected);
        vm.UninstallSelectedCommand.Execute(null);
        Assert.False(vm.Gate.IsOpen);
        Assert.Empty(executor.Plans);
    }

    // ---- The ProgramOwned-only barrier (carried from the retired wizard tests) ----

    /// <summary>
    /// A shared candidate never becomes a runnable action, whatever the user does in the gate. The barrier is
    /// the scanner's classification plus <see cref="LeftoverPlanBuilder"/>'s ProgramOwned-only invariant, and
    /// this asserts the OUTCOME at the executor rather than the mechanism.
    /// </summary>
    [Fact]
    public async Task A_shared_leftover_never_reaches_the_executor()
    {
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(
            RegistryHive.LocalMachine, @"SOFTWARE\SomeVendor", RegistryView.Registry64, "vendor parent"));

        (UninstallViewModel vm, RecordingExecutor executor) = await StagedRemovalAsync(probe);

        PlanRow shared = Assert.Single(vm.Gate.Rows, row => row.IsSkipped);
        Assert.False(shared.IsVetoable);
        Assert.Throws<InvalidOperationException>(() => shared.IsIncluded = true);

        Approve(vm);
        Assert.DoesNotContain(Assert.Single(executor.Plans).Actions,
            action => action is RegistryDeleteAction);
    }

    /// <summary>
    /// Forcing a non-ProgramOwned candidate into the plan builder throws rather than silently dropping it —
    /// the defence-in-depth guard the wizard's fail-loud banner sat on. Composition refuses, so nothing is
    /// staged and nothing can be approved.
    /// </summary>
    [Fact]
    public void Force_injecting_a_shared_candidate_makes_the_composer_refuse_rather_than_drop_it()
    {
        I18n i18n = TestI18n.Full("en");
        var composer = new RemovalPlanComposer(i18n);
        InstalledApp app = TestData.App(uninstall: UninstallString, installLocation: InstallLocation);

        var forged = new LeftoverScanResult(
            new OperationPlan("x", "uninstall", Array.Empty<PlannedAction>(), DateTime.UtcNow),
            Array.Empty<SkippedAction>(),
            new[]
            {
                new LeftoverCandidate
                {
                    Action = TestData.RegKey(RegistryHive.LocalMachine, @"SOFTWARE\SomeVendor"),
                    Classification = LeftoverClassification.Shared,
                    Selected = true,   // the selectability barrier bypassed on purpose
                    GateReason = "vendor parent",
                },
            });

        Assert.Throws<LeftoverPlanBuildException>(
            () => composer.Compose(app, forged, withRestorePoint: false, DateTime.UtcNow));
    }

    // ---- A-M3-1: no marketing mode names, no fake choices ----

    /// <summary>
    /// A-M3-1. The Quick / Standard / Deep scan scopes are gone, along with the strings that admitted they ran
    /// the same scan. Asserted over the SHIPPED string tables rather than over the view, because a mode name
    /// nothing renders today is a mode name the next screen can render tomorrow.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public void The_uninstall_module_ships_no_scan_depth_mode_names(string culture)
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Suite.Module.Uninstall", "lang", $"{culture}.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        string[] depthKeys = document.RootElement.EnumerateObject()
            .Where(property => property.Name.Contains("depth", StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Name)
            .ToArray();

        Assert.True(depthKeys.Length == 0,
            "The scan-depth modes ran the same scan and were replaced by one action: "
            + string.Join(", ", depthKeys));

        // Non-vacuity: the ONE replacement action really is in the table under test.
        Assert.Contains(document.RootElement.EnumerateObject(),
            property => property.Name == "uninstall.removal.scan");
    }

    // ---- The scan announces nothing it cannot yet show ----

    /// <summary>
    /// The scan publishes its rows BEFORE it announces that it has scanned. Asserted at the announcement
    /// itself rather than after it: the observer records <see cref="UninstallViewModel.LeftoverRows"/> and the
    /// destructive door's enablement inside every <c>PropertyChanged</c> for
    /// <see cref="UninstallViewModel.HasScanned"/>, so a view-model that flips the flag first fails here on
    /// every run, on any thread, at any speed — it is not a race this test has to win.
    /// <para>
    /// This is the guard for a real intermittent failure: <c>UninstallScreenTests</c> waited on
    /// <c>HasScanned</c> and asserted the row count, and in a full-suite run the continuation was descheduled
    /// between publishing the flag and filling the rows, so the count came back 0. The same window is a UI
    /// honesty defect in its own right — the rail's "nothing was left behind" line reads
    /// <c>HasScanned</c> together with an empty row list.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_scan_never_claims_to_have_scanned_while_its_rows_are_missing()
    {
        UninstallViewModel vm = BuildVm(out _, Seeded());
        await vm.LoadAsync();
        vm.SelectedRow = Assert.Single(vm.AllRows);

        // Observe at EVERY moment the view-model makes itself observable — a property announcement or a
        // collection change — not only at the ones that mention HasScanned. Both are points a real binding
        // re-reads the screen, so an invariant that holds at all of them holds for the view; and checking
        // them all is what makes this deterministic rather than a race this test has to win. Two different
        // orderings break it, and each is caught at a different callback: publishing the flag before the
        // rows is caught at the property announcement, and publishing it before the CLEAR is caught at the
        // collection reset.
        const int ExpectedRows = 1;
        var violations = new List<string>();
        var observations = 0;

        void Check(string source)
        {
            observations++;
            if (vm.HasScanned && vm.LeftoverRows.Count != ExpectedRows)
            {
                violations.Add(
                    $"{source}: HasScanned was true with {vm.LeftoverRows.Count} row(s), expected {ExpectedRows}");
            }
        }

        vm.PropertyChanged += (_, e) => Check("PropertyChanged(" + e.PropertyName + ")");
        vm.LeftoverRows.CollectionChanged += (_, e) => Check("LeftoverRows." + e.Action);

        vm.ScanLeftoversCommand.Execute(null);
        Settle(() => !vm.IsScanningLeftovers && vm.HasScanned);

        // Non-vacuity: the view-model really did make itself observable, so an implementation that stays
        // silent cannot pass this by never raising anything.
        Assert.True(observations > 0, "the scan produced no observable moment at all.");
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));

        // And the door the rows justify is enabled by the time the scan reports itself finished.
        Assert.True(vm.CanUninstallSelected);
    }

    /// <summary>The mirror: while a scan is in flight the screen must not still present the previous
    /// answer. <c>HasScanned</c> goes false for the whole window rather than flipping true halfway.</summary>
    [Fact]
    public async Task A_rescan_drops_the_previous_answer_before_it_starts()
    {
        (UninstallViewModel vm, _) = await ScannedAsync();
        Assert.True(vm.HasScanned);
        Assert.Single(vm.LeftoverRows);

        bool sawStaleClaim = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UninstallViewModel.IsScanningLeftovers)
                && vm.IsScanningLeftovers
                && vm.HasScanned)
            {
                sawStaleClaim = true;
            }
        };

        vm.ScanLeftoversCommand.Execute(null);
        Settle(() => !vm.IsScanningLeftovers && vm.HasScanned);

        Assert.False(sawStaleClaim,
            "a scan started while the screen still claimed the previous scan's answer.");
        Assert.Single(vm.LeftoverRows);
    }

    [Fact]
    public void The_scope_filter_is_an_inventory_filter_with_three_states()
    {
        UninstallViewModel vm = BuildVm(out _, new FakeLeftoverProbe());

        Assert.True(vm.IsScopeAll);
        vm.ScopeIndex = 1;
        Assert.True(vm.IsScopeDesktop);
        Assert.False(vm.IsScopeAll);
        vm.ScopeIndex = 2;
        Assert.True(vm.IsScopeStore);
    }

    // ===== fixture =====

    private static LeftoverRegistryKey OwnedKey() => new(
        RegistryHive.LocalMachine, @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "owned key");

    private static void Approve(UninstallViewModel vm)
    {
        vm.Gate.TypedConfirm = vm.Gate.ConfirmWord;
        Assert.True(vm.Gate.CanApprove);
        vm.Gate.ApproveCommand.Execute(null);
        Settle(() => vm.HasResult);
    }

    private static async Task<(UninstallViewModel Vm, RecordingExecutor Executor)> StagedRemovalAsync(
        FakeLeftoverProbe? probe = null, bool restorePointAvailable = true)
    {
        (UninstallViewModel vm, RecordingExecutor executor) =
            await ScannedAsync(probe, restorePointAvailable: restorePointAvailable);
        vm.UninstallSelectedCommand.Execute(null);
        return (vm, executor);
    }

    private static async Task<(UninstallViewModel Vm, RecordingExecutor Executor)> ScannedAsync(
        FakeLeftoverProbe? probe = null, string? uninstall = UninstallString, bool restorePointAvailable = true)
    {
        probe ??= Seeded();
        UninstallViewModel vm = BuildVm(out RecordingExecutor executor, probe, uninstall, restorePointAvailable);
        await vm.LoadAsync();
        vm.SelectedRow = Assert.Single(vm.AllRows);
        vm.ScanLeftoversCommand.Execute(null);
        // Wait on the LAST thing the scan writes, not on the flag it publishes partway: IsScanningLeftovers
        // goes false only after the rows, the plan and every announcement are committed.
        Settle(() => !vm.IsScanningLeftovers && vm.HasScanned);
        return (vm, executor);
    }

    private static FakeLeftoverProbe Seeded()
    {
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(OwnedKey());
        return probe;
    }

    private static UninstallViewModel BuildVm(
        out RecordingExecutor executor,
        FakeLeftoverProbe probe,
        string? uninstall = UninstallString,
        bool restorePointAvailable = true)
    {
        executor = new RecordingExecutor();
        InstalledApp app = TestData.App(uninstall: uninstall, installLocation: InstallLocation);
        return new UninstallViewModel(
            TestI18n.Full("en"),
            new SingleAppReader(app),
            new NoAppxReader(),
            TestData.Gate(),
            probe,
            executor,
            new NoFolderOpener(),
            new FixedRestorePointCapability(restorePointAvailable));
    }

    /// <summary>Drives the scan/run continuations, which hop back through Task.Run without a dispatcher.</summary>
    private static void Settle(Func<bool> until)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!until() && DateTime.UtcNow < deadline)
            Thread.Sleep(5);
        Assert.True(until(), "the uninstall fixture did not settle in time");
    }

    private sealed class RecordingExecutor : IPlanExecutor
    {
        public List<OperationPlan> Plans { get; } = new();
        public List<string> Hashes { get; } = new();

        public PlanExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash)
        {
            Plans.Add(plan);
            Hashes.Add(approvedPlanHash);
            return new PlanExecutionReport(
                true,
                approvedPlanHash,
                plan.Actions
                    .Select(a => new PlanActionResult(a.Id, a.Kind, PlanActionStatus.Done, "recorded"))
                    .ToArray());
        }
    }

    private sealed class SingleAppReader(InstalledApp app) : IInstalledAppReader
    {
        public IReadOnlyList<InstalledApp> ReadAll() => new[] { app };
    }

    private sealed class NoAppxReader : IAppxReader
    {
        public IReadOnlyList<InstalledAppx> ReadCurrentUserPackages() => Array.Empty<InstalledAppx>();
    }

    private sealed class NoFolderOpener : IFolderOpener
    {
        public void OpenFolder(string path) { }
    }

    private sealed class FixedRestorePointCapability(bool available) : IRestorePointCapabilityProbe
    {
        public bool IsAvailable() => available;
    }
}

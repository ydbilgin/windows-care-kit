using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Module.Clean.ViewModels;
using WindowsCareKit.Tests.Execution;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// The Temizle (Clean) view-model command-flow wiring (host-safe). It drives the four read-only sections over
/// FAKE probes and the REAL <see cref="GatedExecutor"/> sitting on RECORDING adapters, so:
/// junk runs the EXACT previewed plan's hash through the executor; the recycle bin's irreversible empty is a
/// TWO-STEP confirm whose recording emptier is NOT called until confirm (fail-without / pass-with); a
/// gate-blocked startup action shows skipped, never executed; and extensions are inventory-only (the folder
/// opener, never the executor). No real filesystem, registry, recycle bin, or Explorer is touched.
/// </summary>
public sealed class CleanViewModelTests
{
    // Async settle ceiling mirrors UninstallExecutionTests.PumpAsync (flaky-fix 2026-06-21): the happy path
    // exits the instant until() is true (~ms); the large cap only prevents false timeouts under CI/Release load.
    private static async Task PumpAsync(Func<bool> until, int timeoutMs = 30_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10);
        Assert.True(until(), "operation did not complete in time");
    }

    private sealed class FakeJunkProbe(params JunkCandidate[] candidates) : IJunkProbe
    {
        public IReadOnlyList<JunkCandidate> FindJunk() => candidates;
    }

    private sealed class FakeStartupProbe(StartupInventory inventory) : IStartupProbe
    {
        public FakeStartupProbe(params StartupEntry[] entries)
            : this(new StartupInventory(entries, SourceHealth.Complete, Array.Empty<InventorySourceFault>())) { }

        public StartupInventory ReadAll() => inventory;
    }

    private sealed class FakeRecycleBinService(RecycleBinInventory inventory) : IRecycleBinService
    {
        public FakeRecycleBinService(RecycleBinStats stats) : this(RecycleBinInventory.Complete(stats)) { }

        public RecycleBinInventory Query() => inventory;
    }

    private sealed class FakeBrowserExtensionInventory(BrowserExtensionListing listing) : IBrowserExtensionInventory
    {
        public FakeBrowserExtensionInventory(params BrowserExtension[] exts)
            : this(new BrowserExtensionListing(exts, SourceHealth.Complete, Array.Empty<InventorySourceFault>())) { }

        public BrowserExtensionListing ReadAll() => listing;
    }

    private sealed class RecordingFolderOpener : IFolderOpener
    {
        public string? LastPath { get; private set; }
        public int CallCount { get; private set; }
        public void OpenFolder(string path) { CallCount++; LastPath = path; } // never launches Explorer
    }

    private static CleanViewModel BuildVm(
        ExecutorFixture fx,
        RecordingFolderOpener opener,
        IJunkProbe? junk = null,
        IStartupProbe? startup = null,
        IBrowserExtensionInventory? extensions = null,
        IRecycleBinService? recycle = null,
        I18n? i18n = null)
    {
        return new CleanViewModel(
            i18n ?? new I18n(),
            junk ?? new FakeJunkProbe(),
            startup ?? new FakeStartupProbe(),
            extensions ?? new FakeBrowserExtensionInventory(),
            recycle ?? new FakeRecycleBinService(new RecycleBinStats(0, 0)),
            opener,
            fx.Gate,
            new FixturePlanExecutor(fx.Executor));
    }

    private sealed class FixturePlanExecutor(GatedExecutor executor) : IPlanExecutor
    {
        public PlanExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash)
        {
            ExecutionReport report = executor.ExecuteWithReport(plan, approvedPlanHash);
            return new PlanExecutionReport(
                report.Authorized,
                report.PlanHash,
                report.Results
                    .Select(r => new PlanActionResult(r.ActionId, r.Kind, MapStatus(r.Status), r.Detail))
                    .ToArray());
        }

        private static PlanActionStatus MapStatus(ActionStatus status) => status switch
        {
            ActionStatus.Done => PlanActionStatus.Done,
            ActionStatus.Skipped => PlanActionStatus.Skipped,
            ActionStatus.Blocked => PlanActionStatus.Blocked,
            ActionStatus.Failed => PlanActionStatus.Failed,
            ActionStatus.NotRun => PlanActionStatus.NotRun,
            _ => PlanActionStatus.Failed,
        };
    }

    // ---- junk: scan builds a dry-run plan; approving executes the EXACT selected subset's hash ----

    /// <summary>A-M2-1. Nothing destructive is reachable before a plan exists — and, after M2, not even then:
    /// every junk delete the engine produces is best-effort, so spec §2.1 rule 3 starts every row EXCLUDED and
    /// the approve door stays shut until the user opts something in.</summary>
    [Fact]
    public async Task ScanJunk_builds_a_dry_run_preview_and_the_approve_door_stays_shut_until_a_row_is_selected()
    {
        using var fx = new ExecutorFixture();
        var vm = BuildVm(fx, new RecordingFolderOpener(),
            junk: new FakeJunkProbe(new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 2048, "User temp folder")));

        Assert.False(vm.ApprovePlanCommand.CanExecute(null));   // no plan yet → disabled

        vm.ScanJunkCommand.Execute(null);
        await PumpAsync(() => vm.JunkScanned);

        Assert.Single(vm.JunkPreview);                          // the temp folder became one previewed delete
        Assert.True(vm.JunkPreview[0].IsVetoable);              // the engine acts at this row's granularity
        Assert.False(vm.JunkPreview[0].IsIncluded);             // best-effort → never pre-checked
        Assert.Equal(0, vm.SelectedActionCount);
        Assert.False(vm.ApprovePlanCommand.CanExecute(null));   // a plan exists but nothing is selected
        Assert.Empty(fx.Adapters.Calls);                        // scan is read-only: NOTHING executed yet

        vm.JunkPreview[0].IsIncluded = true;
        Assert.Equal(1, vm.SelectedActionCount);
        Assert.True(vm.ApprovePlanCommand.CanExecute(null));
    }

    /// <summary>
    /// A-M2-3. Approving executes EXACTLY the subset: the plan handed to the executor carries
    /// <see cref="CleanViewModel.SelectedActionCount"/> actions, and the hash it is authorized against is that
    /// subset's own hash — not the superset it was composed out of (spec §1.1). Two candidates are scanned and
    /// one is vetoed, so a regression that ran the whole plan would dispatch the vetoed path too.
    /// </summary>
    [Fact]
    public async Task Approving_runs_exactly_the_selected_subset_under_the_subsets_own_hash()
    {
        using var fx = new ExecutorFixture();
        var kept = new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 2048, "User temp folder");
        var vetoed = new JunkCandidate(@"C:\Users\alice\AppData\Local\CrashDumps", 4096, "Crash dumps");
        var vm = BuildVm(fx, new RecordingFolderOpener(), junk: new FakeJunkProbe(kept, vetoed));

        vm.ScanJunkCommand.Execute(null);
        await PumpAsync(() => vm.JunkScanned);
        Assert.Equal(2, vm.JunkPreview.Count);

        vm.JunkPreview[0].IsIncluded = true;                    // keep the temp folder, veto the crash dumps
        Assert.Equal(1, vm.SelectedActionCount);

        vm.ApprovePlanCommand.Execute(null);

        // The composed plan is fully reversible, so approving RUNS. If it stages the irreversible confirm
        // instead, the composition kept an action the user vetoed — named here rather than left to surface as
        // a timeout waiting for a result that was never coming.
        Assert.False(vm.RecycleConfirmPending,
            "a fully reversible subset must run on approve; staging the confirm means the composed plan still "
            + "carried the Recycle-Bin empty the user never selected");
        await PumpAsync(() => vm.HasResult);

        // Exactly the selected delete was dispatched — the vetoed one never reached an adapter.
        FileDeleteAction ran = Assert.Single(fx.Adapters.Dispatched.OfType<FileDeleteAction>());
        Assert.Equal(@"C:\Users\alice\AppData\Local\Temp", ran.Path);

        // The hash the executor authorized against is the SUBSET's hash. Rebuilt independently here, from the
        // one action that survived — so computing it from the full plan (MP-2) cannot satisfy this.
        var subset = new OperationPlan(
            "Clean", "clean", new PlannedAction[] { ran }, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(subset.ComputeHash(), LoggedPlanHash(fx));

        Assert.Contains(fx.LogLines(), l => l.Contains("plan.done") && l.Contains("\"done\":\"1\""));
        Assert.True(vm.HasResult); // a result summary was produced after the run
    }

    /// <summary>A-M2-2. The Recycle-Bin row exists, is vetoable, reports <see cref="UndoCapability.None"/>, and
    /// is UNSELECTED on a fresh view model — so a fresh screen counts zero permanent selections.</summary>
    [Fact]
    public void A_fresh_view_model_selects_nothing_and_the_recycle_row_is_unselected_with_no_recovery()
    {
        using var fx = new ExecutorFixture();
        var vm = BuildVm(fx, new RecordingFolderOpener());

        Assert.True(vm.RecycleRow.IsVetoable);
        Assert.False(vm.RecycleRow.IsIncluded);
        Assert.Equal(UndoCapability.None, vm.RecycleRow.Recovery);

        Assert.Equal(0, vm.SelectedActionCount);
        Assert.Equal(0, vm.RecoveryNoneCount);      // nothing permanent is selected on a fresh screen
        Assert.Equal(0, vm.RecoveryFullCount);
        Assert.Equal(0, vm.RecoveryPartialCount);
        Assert.Equal(0, vm.RecoveryUnknownCount);
    }

    /// <summary>
    /// A-M2-4. Unchecking a row moves both the action count and the recovery counts, and a language switch
    /// moves neither — the counts are arithmetic over <see cref="PlanRow.Recovery"/>, and a rendered string is
    /// never parsed (spec §1.2). The row's own rendered undo text is asserted to CHANGE in the same switch, so
    /// this is not vacuously true of a table that failed to load.
    /// </summary>
    [Fact]
    public async Task Unchecking_a_row_moves_the_counts_and_a_language_switch_moves_none_of_them()
    {
        using var fx = new ExecutorFixture();
        var vm = BuildVm(fx, new RecordingFolderOpener(),
            junk: new FakeJunkProbe(
                new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 2048, "User temp folder"),
                new JunkCandidate(@"C:\Users\alice\AppData\Local\CrashDumps", 4096, "Crash dumps")),
            i18n: TestI18n.Full("en"));

        vm.ScanJunkCommand.Execute(null);
        await PumpAsync(() => vm.JunkScanned);

        vm.JunkPreview[0].IsIncluded = true;
        vm.JunkPreview[1].IsIncluded = true;
        vm.RecycleRow.IsIncluded = true;
        Assert.Equal(3, vm.SelectedActionCount);
        Assert.Equal(2, vm.RecoveryFullCount);   // junk deletes recover from the recycle bin
        Assert.Equal(1, vm.RecoveryNoneCount);   // emptying the bin does not

        vm.JunkPreview[1].IsIncluded = false;
        Assert.Equal(2, vm.SelectedActionCount);
        Assert.Equal(1, vm.RecoveryFullCount);
        Assert.Equal(1, vm.RecoveryNoneCount);

        string undoBefore = vm.JunkPreview[0].Undo;
        vm.I18n.Load("tr");

        Assert.NotEqual(undoBefore, vm.JunkPreview[0].Undo);  // the RENDERED text really did switch language
        Assert.Equal(2, vm.SelectedActionCount);              // …and not one count moved with it
        Assert.Equal(1, vm.RecoveryFullCount);
        Assert.Equal(1, vm.RecoveryNoneCount);
        Assert.Equal(0, vm.RecoveryPartialCount);
        Assert.Equal(0, vm.RecoveryUnknownCount);
    }

    /// <summary>A-M2-7. The Technical-details toggle ADDS detail and nothing else: it is not an input to the
    /// plan, the selection, or any count.</summary>
    [Fact]
    public async Task Technical_details_toggle_changes_no_plan_no_selection_and_no_count()
    {
        using var fx = new ExecutorFixture();
        var vm = BuildVm(fx, new RecordingFolderOpener(),
            junk: new FakeJunkProbe(new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 2048, "User temp folder")));

        vm.ScanJunkCommand.Execute(null);
        await PumpAsync(() => vm.JunkScanned);
        vm.JunkPreview[0].IsIncluded = true;

        (int actions, int full, int partial, int none, string consequence, bool included) before =
            (vm.SelectedActionCount, vm.RecoveryFullCount, vm.RecoveryPartialCount, vm.RecoveryNoneCount,
             vm.ConsequenceSentence, vm.JunkPreview[0].IsIncluded);

        Assert.True(vm.TechnicalDetails);
        vm.TechnicalDetails = false;
        vm.TechnicalDetails = true;
        vm.TechnicalDetails = false;

        Assert.Equal(before,
            (vm.SelectedActionCount, vm.RecoveryFullCount, vm.RecoveryPartialCount, vm.RecoveryNoneCount,
             vm.ConsequenceSentence, vm.JunkPreview[0].IsIncluded));
        Assert.Empty(fx.Adapters.Calls);   // and it certainly never runs anything
    }

    /// <summary>
    /// Approving a selection that contains something irreversible stages the INLINE confirm rather than
    /// running — the proportional ceremony decision §2.1 names correct, kept and not replaced by a gate. The
    /// emptier is not called until the confirm is approved (fail-without / pass-with).
    /// </summary>
    [Fact]
    public async Task Approving_a_selection_containing_the_recycle_bin_stages_the_inline_confirm_first()
    {
        using var fx = new ExecutorFixture();
        var vm = BuildVm(fx, new RecordingFolderOpener(),
            junk: new FakeJunkProbe(new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 2048, "User temp folder")));

        vm.ScanJunkCommand.Execute(null);
        await PumpAsync(() => vm.JunkScanned);
        vm.JunkPreview[0].IsIncluded = true;
        vm.RecycleRow.IsIncluded = true;

        vm.ApprovePlanCommand.Execute(null);

        Assert.True(vm.RecycleConfirmPending);           // staged, not run
        Assert.Equal(0, fx.RecycleBinEmptier.CallCount); // FAIL-WITHOUT: nothing emptied at the approve step
        Assert.Empty(fx.Adapters.Dispatched);            // and no junk delete ran either

        vm.ConfirmEmptyRecycleCommand.Execute(null);
        await PumpAsync(() => vm.HasResult);

        Assert.Equal(1, fx.RecycleBinEmptier.CallCount); // PASS-WITH: emptied exactly once after the confirm
        Assert.Single(fx.Adapters.Dispatched.OfType<FileDeleteAction>());
    }

    /// <summary>
    /// Action order is an execution-safety property. The bin is emptied BEFORE the junk deletes, because a bin
    /// emptied after them would destroy the very Recycle-Bin copies those rows state as their undo — the
    /// screen would print "recovers from the Recycle Bin" about files the same run had just made permanent.
    /// </summary>
    [Fact]
    public async Task The_composed_plan_empties_the_bin_before_it_deletes_junk_into_it()
    {
        using var fx = new ExecutorFixture();
        var vm = BuildVm(fx, new RecordingFolderOpener(),
            junk: new FakeJunkProbe(new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 2048, "User temp folder")));

        vm.ScanJunkCommand.Execute(null);
        await PumpAsync(() => vm.JunkScanned);
        vm.JunkPreview[0].IsIncluded = true;
        vm.RecycleRow.IsIncluded = true;

        vm.ApprovePlanCommand.Execute(null);
        vm.ConfirmEmptyRecycleCommand.Execute(null);
        await PumpAsync(() => vm.HasResult);

        string[] kinds = fx.LogLines()
            .Where(line => line.Contains("action.done", StringComparison.Ordinal))
            .Select(line => line.Contains("recyclebin.empty", StringComparison.Ordinal) ? "empty" : "delete")
            .ToArray();

        Assert.Equal(new[] { "empty", "delete" }, kinds);
    }

    /// <summary>
    /// A-M2-5 (view-model half). A row the gate refused is kept, carries its reason, and can never be selected
    /// — and it never enters the composed plan. The render half is in <c>CleanScreenTests</c>.
    /// </summary>
    [Fact]
    public async Task A_gate_refused_junk_row_is_shown_with_its_reason_and_is_not_selectable()
    {
        using var fx = new ExecutorFixture();
        // Under the Windows directory → the gate refuses the delete outright, so it is previewed as BLOCKED.
        var vm = BuildVm(fx, new RecordingFolderOpener(),
            junk: new FakeJunkProbe(
                new JunkCandidate(@"C:\Windows\System32\wck-not-junk", 2048, "Protected"),
                new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 2048, "User temp folder")));

        vm.ScanJunkCommand.Execute(null);
        await PumpAsync(() => vm.JunkScanned);

        PlanRow blocked = Assert.Single(vm.JunkSkipped);
        Assert.True(blocked.IsSkipped);
        Assert.False(blocked.IsVetoable);                          // no veto control may be offered for it
        Assert.False(string.IsNullOrWhiteSpace(blocked.Detail));   // the reason is carried, not dropped
        Assert.Equal(1, vm.ProtectedCount);

        // Selecting everything selectable still runs only the one allowed delete.
        vm.JunkPreview[0].IsIncluded = true;
        vm.ApprovePlanCommand.Execute(null);
        Assert.False(vm.RecycleConfirmPending, "the composed plan must not carry an unselected irreversible action");
        await PumpAsync(() => vm.HasResult);

        FileDeleteAction ran = Assert.Single(fx.Adapters.Dispatched.OfType<FileDeleteAction>());
        Assert.Equal(@"C:\Users\alice\AppData\Local\Temp", ran.Path);
    }

    private static string LoggedPlanHash(ExecutorFixture fx)
    {
        foreach (string line in fx.LogLines())
        {
            const string marker = "\"planHash\":";
            int i = line.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0)
                continue;
            int start = line.IndexOf('"', i + marker.Length) + 1;
            int end = line.IndexOf('"', start);
            if (start > 0 && end > start)
                return line.Substring(start, end - start);
        }
        return string.Empty;
    }

    // ---- recycle bin: two-step irreversibility (the load-bearing fail-without/pass-with proof) ----

    [Fact]
    public void EmptyRecycle_only_stages_a_confirm_and_does_NOT_call_the_emptier()
    {
        using var fx = new ExecutorFixture();
        var vm = BuildVm(fx, new RecordingFolderOpener());

        Assert.False(vm.RecycleConfirmPending);
        vm.EmptyRecycleCommand.Execute(null);

        Assert.True(vm.RecycleConfirmPending);  // staged the confirm panel
        Assert.Equal(0, fx.RecycleBinEmptier.CallCount); // FAIL-WITHOUT proof: nothing emptied at the stage step
    }

    [Fact]
    public async Task ConfirmEmptyRecycle_routes_the_empty_action_through_the_gated_executor()
    {
        using var fx = new ExecutorFixture();
        var vm = BuildVm(fx, new RecordingFolderOpener());

        vm.EmptyRecycleCommand.Execute(null);   // stage
        Assert.Equal(0, fx.RecycleBinEmptier.CallCount); // still not emptied before confirm (pass-with baseline)

        vm.ConfirmEmptyRecycleCommand.Execute(null);
        await PumpAsync(() => vm.HasResult);

        Assert.Equal(1, fx.RecycleBinEmptier.CallCount); // PASS-WITH: emptied exactly once after confirm
        Assert.False(vm.RecycleConfirmPending);  // confirm consumed
        Assert.Contains(fx.LogLines(), l => l.Contains("plan.start") && l.Contains("Empty Recycle Bin"));
        Assert.Contains(fx.LogLines(), l => l.Contains("action.done") && l.Contains("recyclebin.empty"));
    }

    [Fact]
    public void CancelEmptyRecycle_clears_the_pending_confirm_without_emptying()
    {
        using var fx = new ExecutorFixture();
        var vm = BuildVm(fx, new RecordingFolderOpener());

        vm.EmptyRecycleCommand.Execute(null);
        Assert.True(vm.RecycleConfirmPending);

        vm.CancelEmptyRecycleCommand.Execute(null);

        Assert.False(vm.RecycleConfirmPending);  // pending cleared
        Assert.Equal(0, fx.RecycleBinEmptier.CallCount); // cancel never empties
    }

    // ---- startup: gate-blocked action shows skipped, never executed ----

    [Fact]
    public async Task Selecting_a_startup_row_builds_a_gate_evaluated_preview()
    {
        using var fx = new ExecutorFixture();
        // An HKCU Run value-delete is gate-ALLOWED (the Run/RunOnce carve-out) → previewed as a runnable row.
        var entry = new StartupEntry("Steam", @"C:\Steam\steam.exe -silent", StartupSource.HkcuRun, FolderPath: null);
        var vm = BuildVm(fx, new RecordingFolderOpener(),
            startup: new FakeStartupProbe(entry));

        vm.LoadStartupCommand.Execute(null);
        await PumpAsync(() => vm.StartupEntries.Count == 1);

        vm.SelectedStartup = vm.StartupEntries[0];

        PlanRow row = Assert.Single(vm.StartupPreview);
        Assert.NotEqual("BLOCKED", row.RiskText); // the allowed Run value-delete is a runnable preview row
        Assert.Empty(fx.Adapters.Calls);          // building a preview executes NOTHING
    }

    [Fact]
    public async Task A_gate_blocked_startup_action_shows_as_skipped_not_executed()
    {
        using var fx = new ExecutorFixture();
        // A Startup-folder shortcut UNDER the Windows directory → a FileDeleteAction the gate refuses outright
        // ("inside the Windows directory"), so the preview row is BLOCKED, never runnable.
        string lnk = @"C:\Windows\System32\wck-evil-startup\Evil.lnk";
        var entry = new StartupEntry("Evil", lnk, StartupSource.StartupFolder, FolderPath: lnk);
        var vm = BuildVm(fx, new RecordingFolderOpener(),
            startup: new FakeStartupProbe(entry));

        vm.LoadStartupCommand.Execute(null);
        await PumpAsync(() => vm.StartupEntries.Count == 1);

        vm.SelectedStartup = vm.StartupEntries[0];

        PlanRow row = Assert.Single(vm.StartupPreview);
        Assert.Equal("BLOCKED", row.RiskText);    // FromSkipped — the protected-dir delete is refused, not runnable

        // Even if the user tries to disable it, the blocked plan reaches the executor but NOTHING is dispatched.
        vm.DisableStartupCommand.Execute(null);
        await PumpAsync(() => vm.HasResult);
        Assert.Empty(fx.Adapters.Dispatched); // gate re-blocks at execution time → zero adapter calls
    }

    // ---- extensions: inventory-only; open-folder goes through the opener, never the executor ----

    [Fact]
    public async Task Extensions_are_inventory_only_and_OpenExtensionFolder_uses_the_folder_opener_not_the_executor()
    {
        using var fx = new ExecutorFixture();
        var opener = new RecordingFolderOpener();
        var ext = new BrowserExtension("Chrome", "Default", "abcdef", "Some Ext", @"C:\Users\alice\AppData\Local\Chrome\Ext\abcdef");
        var vm = BuildVm(fx, opener,
            extensions: new FakeBrowserExtensionInventory(ext));

        vm.LoadExtensionsCommand.Execute(null);
        await PumpAsync(() => vm.Extensions.Count == 1);

        vm.OpenExtensionFolderCommand.Execute(ext);

        Assert.Equal(1, opener.CallCount);                              // routed through IFolderOpener
        Assert.Equal(ext.FolderPath, opener.LastPath);
        Assert.Empty(fx.Adapters.Calls);                               // never the destructive executor
    }

    // ---- NEW-07: read-adapter source health is surfaced honestly, never a fake empty ----

    [Fact]
    public async Task Recycle_query_failure_shows_a_health_note_and_no_fabricated_zero_totals()
    {
        using var fx = new ExecutorFixture();
        var vm = BuildVm(fx, new RecordingFolderOpener(),
            recycle: new FakeRecycleBinService(RecycleBinInventory.Unavailable("HRESULT 0x80004005")));

        vm.RefreshRecycleCommand.Execute(null);
        await PumpAsync(() => !vm.IsBusy);

        Assert.False(string.IsNullOrEmpty(vm.RecycleHealthNote)); // "could not inspect" is surfaced
        Assert.Equal(string.Empty, vm.RecycleStats);              // NOT a fake "0 items · ~0 B"
    }

    [Fact]
    public async Task Recycle_complete_query_shows_totals_and_no_health_note()
    {
        using var fx = new ExecutorFixture();
        var vm = BuildVm(fx, new RecordingFolderOpener(),
            recycle: new FakeRecycleBinService(new RecycleBinStats(3, 2048)));

        vm.RefreshRecycleCommand.Execute(null);
        await PumpAsync(() => !vm.IsBusy);

        Assert.Equal(string.Empty, vm.RecycleHealthNote);         // healthy → no caution
        Assert.False(string.IsNullOrEmpty(vm.RecycleStats));      // totals shown
    }

    [Fact]
    public async Task Startup_partial_read_keeps_the_entries_read_and_shows_an_incomplete_note()
    {
        using var fx = new ExecutorFixture();
        var entry = new StartupEntry("Steam", @"C:\Steam\steam.exe", StartupSource.HkcuRun, FolderPath: null);
        var partial = new StartupInventory(
            new[] { entry },
            SourceHealth.Partial,
            new[] { new InventorySourceFault("HKLM Run", "SecurityException") });
        var vm = BuildVm(fx, new RecordingFolderOpener(), startup: new FakeStartupProbe(partial));

        vm.LoadStartupCommand.Execute(null);
        await PumpAsync(() => !vm.IsBusy && vm.StartupEntries.Count == 1);

        Assert.Single(vm.StartupEntries);                         // partial data preserved
        Assert.False(string.IsNullOrEmpty(vm.StartupHealthNote)); // incompleteness surfaced
    }

    [Fact]
    public async Task Extensions_partial_read_keeps_items_and_shows_an_incomplete_note()
    {
        using var fx = new ExecutorFixture();
        var ext = new BrowserExtension("Chrome", "Default", "abcdef", "Some Ext", @"C:\U\abcdef");
        var partial = new BrowserExtensionListing(
            new[] { ext },
            SourceHealth.Partial,
            new[] { new InventorySourceFault("Edge/Default", "UnauthorizedAccessException") });
        var vm = BuildVm(fx, new RecordingFolderOpener(), extensions: new FakeBrowserExtensionInventory(partial));

        vm.LoadExtensionsCommand.Execute(null);
        await PumpAsync(() => !vm.IsBusy && vm.Extensions.Count == 1);

        Assert.Single(vm.Extensions);                                 // partial data preserved
        Assert.False(string.IsNullOrEmpty(vm.ExtensionsHealthNote));  // incompleteness surfaced
    }
}

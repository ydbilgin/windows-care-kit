using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
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

    private sealed class FakeStartupProbe(params StartupEntry[] entries) : IStartupProbe
    {
        public IReadOnlyList<StartupEntry> ReadAll() => entries;
    }

    private sealed class FakeBrowserExtensionInventory(params BrowserExtension[] exts) : IBrowserExtensionInventory
    {
        public IReadOnlyList<BrowserExtension> ReadAll() => exts;
    }

    private sealed class FakeRecycleBinService(RecycleBinStats stats) : IRecycleBinService
    {
        public RecycleBinStats Query() => stats;
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
        IRecycleBinService? recycle = null)
    {
        var i18n = new I18n();
        return new CleanViewModel(
            i18n,
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

    // ---- junk: scan builds a dry-run plan; run executes the EXACT previewed hash ----

    [Fact]
    public async Task ScanJunk_builds_a_dry_run_preview_and_RunJunk_is_disabled_until_it_is_non_empty()
    {
        using var fx = new ExecutorFixture();
        var vm = BuildVm(fx, new RecordingFolderOpener(),
            junk: new FakeJunkProbe(new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 2048, "User temp folder")));

        Assert.False(vm.RunJunkCommand.CanExecute(null));   // no plan yet → disabled

        vm.ScanJunkCommand.Execute(null);
        await PumpAsync(() => vm.JunkScanned);

        Assert.Single(vm.JunkPreview);                       // the temp folder became one previewed delete
        Assert.True(vm.RunJunkCommand.CanExecute(null));     // non-empty plan → run enabled
        Assert.Empty(fx.Adapters.Calls);                     // scan is read-only: NOTHING executed yet
    }

    [Fact]
    public async Task RunJunk_runs_the_exact_previewed_plan_hash_through_the_gated_executor()
    {
        using var fx = new ExecutorFixture();
        var candidate = new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 2048, "User temp folder");
        var vm = BuildVm(fx, new RecordingFolderOpener(),
            junk: new FakeJunkProbe(candidate));

        vm.ScanJunkCommand.Execute(null);
        await PumpAsync(() => vm.JunkScanned);
        Assert.Single(vm.JunkPreview);

        vm.RunJunkCommand.Execute(null);
        await PumpAsync(() => vm.HasResult);

        // The recording file adapter received EXACTLY the previewed temp-folder delete (non-vacuous: a regression
        // that bypassed the executor or sent a different target would change this recorded value).
        FileDeleteAction ran = Assert.Single(fx.Adapters.Dispatched.OfType<FileDeleteAction>());
        Assert.Equal(@"C:\Users\alice\AppData\Local\Temp", ran.Path);

        // And the hash the executor authorized against is the previewed plan's hash.
        var rebuilt = new JunkScanner(new FakeJunkProbe(candidate), fx.Gate)
            .Scan(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Plan;
        Assert.Equal(rebuilt.ComputeHash(), LoggedPlanHash(fx));

        // The plan.done log records exactly one completed action (DoneCount=1) — the non-vacuous execution proof.
        Assert.Contains(fx.LogLines(), l => l.Contains("plan.done") && l.Contains("\"done\":\"1\""));
        Assert.True(vm.HasResult); // a result summary was produced after the run
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
}

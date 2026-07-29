using System.IO;
using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Localization;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Module.Install.ViewModels;
using WindowsCareKit.Tests.Execution;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// The Kur (Install/Restore) view-model command-flow wiring (host-safe). It drives LoadManifest → BuildPlan →
/// Approve → Run over a FAKE manifest loader, the REAL <see cref="InstallPlanner"/>, and the REAL
/// <see cref="GatedExecutor"/> sitting on RECORDING adapters — so "nothing runs without approval" is proven by
/// ZERO recorded dispatches before approve and the EXACT previewed plan + its hash after. The checkpoint is a
/// fake <see cref="IRestoreStateStore"/>; the host-safe export writes <c>install_plan.json</c> into a temp root
/// through the gate. No winget/npm ever runs, no process is spawned, no real registry/profile is touched.
/// </summary>
public sealed class InstallViewModelTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static InstallEntry Winget(string id, string wingetId, bool admin = false, int order = 100)
        => new(id, "install", "winget", InstallMethod.Winget, wingetId, null, admin, false, order, $"Install {id}");

    private static InstallEntry Npm(string id, string pkg, int order = 200)
        => new(id, "install", "ai-cli", InstallMethod.Npm, null, pkg, false, false, order, $"npm {id}");

    /// <summary>A manifest loader that ignores the path and returns the entries the test supplied.</summary>
    private sealed class FakeManifestLoader(params InstallEntry[] entries) : IInstallManifestLoader
    {
        public InstallManifestLoadResult Load(string manifestPath)
            => InstallManifestLoadResult.Loaded(new InstallManifest(entries), manifestPath);
        public InstallManifestLoadResult Parse(string json)
            => InstallManifestLoadResult.Loaded(new InstallManifest(entries), "<memory>");
    }

    private sealed class ResultManifestLoader(InstallManifestLoadResult result) : IInstallManifestLoader
    {
        public InstallManifestLoadResult Load(string manifestPath) => result;
        public InstallManifestLoadResult Parse(string json) => result;
    }

    /// <summary>A driver guard confirming every identifier as Net so a Net-driver entry is never class-skipped.</summary>
    private sealed class AllNetDriverGuard : IDriverGuard
    {
        public bool IsNetClass(string driverIdentifier) => true;
    }

    /// <summary>An in-memory <see cref="IRestoreStateStore"/> — records every Save so the checkpoint is asserted.</summary>
    private sealed class RecordingStateStore : IRestoreStateStore
    {
        private readonly Dictionary<string, RestoreState> _byDir = new(StringComparer.OrdinalIgnoreCase);
        public int SaveCount { get; private set; }
        public RestoreState? LastSaved { get; private set; }
        public RestoreStateLoad? LoadResultOverride { get; set; }

        public RestoreState Load(string stateDirectory)
            => TryLoad(stateDirectory).State;

        public RestoreStateLoad TryLoad(string stateDirectory)
        {
            if (LoadResultOverride is not null)
                return LoadResultOverride;

            return _byDir.TryGetValue(stateDirectory, out RestoreState? state)
                ? RestoreStateLoad.Loaded(state)
                : RestoreStateLoad.Missing;
        }

        public void Save(string stateDirectory, RestoreState state)
        {
            SaveCount++;
            LastSaved = state;
            _byDir[stateDirectory] = state;
        }

        public string PathFor(string stateDirectory) => Path.Combine(stateDirectory, ".kurulum_state.json");
    }

    private sealed class FakeAuthProbe : IAuthProbe
    {
        public bool Exists(string path) => false; // existence-only; never reads content
    }

    /// <summary>Build the VM over the real planner + real GatedExecutor on recording adapters (the fixture).</summary>
    private static InstallViewModel BuildVm(
        ExecutorFixture fx,
        RecordingStateStore stateStore,
        InstallRunner runner,
        params InstallEntry[] entries)
        => BuildVm(fx, stateStore, runner, new FakeManifestLoader(entries), new I18n());

    private static InstallViewModel BuildVm(
        ExecutorFixture fx,
        RecordingStateStore stateStore,
        InstallRunner runner,
        IInstallManifestLoader loader,
        I18n i18n)
    {
        var planner = new InstallPlanner(fx.Gate, new AllNetDriverGuard());
        return new InstallViewModel(
            i18n, loader, planner, new FakeAuthProbe(), stateStore, fx.Gate, new GatedPlanExecutor(fx.Executor), runner);
    }

    private static InstallRunner NoopRunner()
        => new(new ThrowingPlanWriter(), new FakeClock(T0));

    /// <summary>A plan writer that must never be called by the destructive Run/BuildPlan paths (only ExportPlan).</summary>
    private sealed class ThrowingPlanWriter : IInstallPlanWriter
    {
        public string WriteExport(InstallPlanExportDoc doc, string payloadRoot, ISafetyGate gate)
            => throw new InvalidOperationException("ExportPlan must not be invoked by this test path.");
    }

    private sealed class BlockingPlanExecutor : IPlanExecutor, IDisposable
    {
        public ManualResetEventSlim Started { get; } = new(initialState: false);
        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public PlanExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash)
        {
            Started.Set();
            Release.Wait();
            return new PlanExecutionReport(
                Authorized: true,
                PlanHash: approvedPlanHash,
                Results: plan.Actions.Select(action => new PlanActionResult(
                    action.Id, action.Kind, PlanActionStatus.Done, "synthetic completion")).ToArray());
        }

        public void Dispose()
        {
            Started.Dispose();
            Release.Dispose();
        }
    }

    [Fact]
    public void Manifest_outcomes_reach_distinct_visible_state()
    {
        using var fx = new ExecutorFixture(TestData.Gate());
        var stateStore = new RecordingStateStore();
        InstallEntry entry = Winget("git", "Git.Git");
        var cases = new[]
        {
            new InstallManifestLoadResult(InstallManifest.Empty, InstallManifestLoadStatus.NotInstalled,
                @"C:\app\manifests\90-install.json", null),
            InstallManifestLoadResult.Loaded(new InstallManifest([entry]), @"C:\app\manifests\90-install.json"),
            InstallManifestLoadResult.Loaded(InstallManifest.Empty, @"C:\app\manifests\90-install.json"),
            new InstallManifestLoadResult(InstallManifest.Empty, InstallManifestLoadStatus.Malformed,
                @"C:\app\manifests\bad.json", "JsonException"),
            new InstallManifestLoadResult(InstallManifest.Empty, InstallManifestLoadStatus.Unreadable,
                @"C:\app\manifests\locked.json", "IOException"),
        };

        var visibleStates = new HashSet<(string Summary, string Info, string Health)>();
        foreach (InstallManifestLoadResult load in cases)
        {
            InstallViewModel vm = BuildVm(
                fx, stateStore, NoopRunner(), new ResultManifestLoader(load), TestI18n.Full());
            vm.LoadManifest();
            visibleStates.Add((vm.Summary, vm.ManifestInfoNote, vm.ManifestHealthNote));
        }

        Assert.Equal(5, visibleStates.Count);
        // MAJOR-03: Health MUST stay empty for NotInstalled — an absent optional component is the calm,
        // most common production state, not breakage. Without this clause, widening the health-note
        // condition to include NotInstalled leaves the whole suite green.
        Assert.Contains(visibleStates, s => s.Summary == "0 entries loaded" && s.Info.Contains("not installed")
            && s.Health.Length == 0);
        Assert.Contains(visibleStates, s => s.Summary == "1 entries loaded" && s.Info.Length == 0 && s.Health.Length == 0);
        Assert.Contains(visibleStates, s => s.Summary == "0 entries loaded" && s.Info.Length == 0 && s.Health.Length == 0);
        // MINOR-03: the localized cause clause must distinguish corrupt from unreadable — the raw CLR type
        // name that follows it is a diagnostic token, not language, and is untranslated in every locale.
        Assert.Contains(visibleStates, s => s.Health.Contains(@"C:\app\manifests\bad.json") && s.Health.Contains("JsonException")
            && s.Health.Contains("(corrupt, "));
        Assert.Contains(visibleStates, s => s.Health.Contains(@"C:\app\manifests\locked.json") && s.Health.Contains("IOException")
            && s.Health.Contains("(unreadable, "));
    }

    // ---- plan shape ----

    [Fact]
    public void LoadManifest_then_BuildPlan_produces_plan_rows_and_awaits_approval()
    {
        using var fx = new ExecutorFixture();
        var store = new RecordingStateStore();
        var vm = BuildVm(fx, store, NoopRunner(), Winget("git", "Git.Git"), Npm("claude", "@anthropic-ai/claude-code"));

        vm.LoadManifest();
        vm.BuildPlan();

        Assert.True(vm.HasPlan);
        Assert.Equal(2, vm.PlanRows.Count);              // winget + npm both became command actions
        Assert.True(vm.IsAwaitingApproval);              // a plan exists but is not yet approved
        Assert.False(vm.IsPreviewApproved);
    }

    [Fact]
    public void Run_command_is_disabled_until_a_plan_exists_and_is_approved()
    {
        using var fx = new ExecutorFixture();
        var vm = BuildVm(fx, new RecordingStateStore(), NoopRunner(), Winget("git", "Git.Git"));

        Assert.False(vm.RunCommand.CanExecute(null));    // no plan yet
        Assert.False(vm.ApproveCommand.CanExecute(null));

        vm.LoadManifest();
        vm.BuildPlan();
        Assert.False(vm.RunCommand.CanExecute(null));    // plan exists but unapproved → run still disabled
        Assert.True(vm.ApproveCommand.CanExecute(null)); // approve becomes available

        vm.ApproveCommand.Execute(null);
        Assert.True(vm.RunCommand.CanExecute(null));      // HasPlan && approved → run enabled
        Assert.False(vm.ApproveCommand.CanExecute(null)); // already approved → approve disabled
    }

    [Fact]
    public async Task Changing_state_directory_invalidates_the_preview_and_its_approval()
    {
        using var fx = new ExecutorFixture();
        var store = new RecordingStateStore();
        var vm = BuildVm(fx, store, NoopRunner(), Winget("git", "Git.Git"));
        vm.StateDirectory = @"X:\synthetic-state-a";
        vm.LoadManifest();
        vm.BuildPlan();
        vm.ApproveCommand.Execute(null);
        Assert.True(vm.RunCommand.CanExecute(null));

        // The checkpoint participates in planning (already-done entries are skipped). Redirecting it after
        // approval must discard the old plan rather than run/export a plan derived from another checkpoint.
        vm.StateDirectory = @"X:\synthetic-state-b";

        Assert.False(vm.HasPlan);
        Assert.False(vm.IsPreviewApproved);
        Assert.False(vm.RunCommand.CanExecute(null));
        Assert.False(vm.ExportPlanCommand.CanExecute(null));

        await vm.RunAsync();
        Assert.Empty(fx.Adapters.Calls);
        Assert.Equal(0, store.SaveCount);
    }

    // ---- no-run-without-approval (the load-bearing non-vacuous proof) ----

    [Fact]
    public async Task Run_without_approval_records_zero_dispatches_and_writes_no_checkpoint()
    {
        using var fx = new ExecutorFixture();
        var store = new RecordingStateStore();
        var vm = BuildVm(fx, store, NoopRunner(), Winget("git", "Git.Git"));
        vm.StateDirectory = Path.Combine(Path.GetTempPath(), "wck-install-vm-" + Guid.NewGuid().ToString("N"));

        vm.LoadManifest();
        vm.BuildPlan();
        Assert.True(vm.HasPlan);

        // Run WITHOUT approval: the early-return guard must keep the plan out of the executor entirely.
        await vm.RunAsync();

        Assert.Empty(fx.Adapters.Calls);     // ZERO adapter dispatches — the recording proof (fail-without)
        Assert.Equal(0, store.SaveCount);    // and no checkpoint persisted
        Assert.Empty(vm.ExecutionResults);
    }

    [Fact]
    public async Task Run_yields_the_dispatcher_and_refuses_a_second_run_while_busy()
    {
        using var fx = new ExecutorFixture();
        using var executor = new BlockingPlanExecutor();
        var store = new RecordingStateStore();
        var i18n = new I18n();
        var vm = new InstallViewModel(
            i18n,
            new FakeManifestLoader(Winget("git", "Git.Git")),
            new InstallPlanner(fx.Gate, new AllNetDriverGuard()),
            new FakeAuthProbe(),
            store,
            fx.Gate,
            executor,
            NoopRunner())
        {
            StateDirectory = Path.Combine(Path.GetTempPath(), "wck-install-vm-busy"),
        };
        vm.LoadManifest();
        vm.BuildPlan();
        vm.ApproveCommand.Execute(null);

        Task run = vm.RunAsync();

        Assert.False(run.IsCompleted);
        Assert.True(vm.IsBusy);
        Assert.False(vm.RunCommand.CanExecute(null));
        Assert.True(executor.Started.Wait(TimeSpan.FromSeconds(5)));

        executor.Release.Set();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Approve_then_Run_dispatches_exactly_the_previewed_plan_with_its_own_hash()
    {
        using var fx = new ExecutorFixture();
        var store = new RecordingStateStore();
        var vm = BuildVm(fx, store, NoopRunner(),
            Winget("node", "OpenJS.NodeJS.LTS"), Npm("claude", "@anthropic-ai/claude-code", order: 50));

        vm.StateDirectory = Path.Combine(Path.GetTempPath(), "wck-install-vm-" + Guid.NewGuid().ToString("N"));
        vm.LoadManifest();
        vm.BuildPlan();

        // Capture the EXACT previewed plan (typed command + arguments) and its hash before approval/run.
        var previewedFiles = fx.Adapters.Dispatched.OfType<CommandAction>().Select(c => c.FileName).ToArray();
        Assert.Empty(previewedFiles);                            // nothing dispatched yet — the baseline

        vm.ApproveCommand.Execute(null);
        await vm.RunAsync();

        // The recording adapter received EXACTLY the two previewed command actions, in plan order.
        CommandAction[] ran = fx.Adapters.Dispatched.OfType<CommandAction>().ToArray();
        Assert.Equal(2, ran.Length);
        Assert.Equal(2, fx.Adapters.Calls.Count);
        Assert.EndsWith("npm.cmd", ran[0].FileName, StringComparison.OrdinalIgnoreCase);   // npm ordered first (Node-before-CLI handled by planner order)
        Assert.EndsWith("winget.exe", ran[1].FileName, StringComparison.OrdinalIgnoreCase);

        // TOCTOU: the hash the executor authorized against equals the previewed plan's ComputeHash().
        string loggedHash = LoggedPlanHash(fx);
        // Rebuild the SAME plan to recompute the previewed hash deterministically (planner is pure on T0).
        var rebuilt = new InstallPlanner(fx.Gate, new AllNetDriverGuard())
            .BuildPlan(new InstallManifest(new[]
            {
                Winget("node", "OpenJS.NodeJS.LTS"), Npm("claude", "@anthropic-ai/claude-code", order: 50),
            }), RestoreState.Empty, T0).Plan;
        Assert.Equal(rebuilt.ComputeHash(), loggedHash);
    }

    /// <summary>The plan hash the executor authorized against, read back from the JSONL execution log.</summary>
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

    // ---- checkpoint persistence + approval consumed ----

    [Fact]
    public async Task After_Run_the_checkpoint_maps_each_action_to_its_entry_and_approval_is_consumed()
    {
        using var fx = new ExecutorFixture();
        var store = new RecordingStateStore();
        var vm = BuildVm(fx, store, NoopRunner(), Winget("git", "Git.Git"), Npm("claude", "@anthropic-ai/claude-code", order: 50));
        vm.StateDirectory = Path.Combine(Path.GetTempPath(), "wck-install-vm-" + Guid.NewGuid().ToString("N"));

        vm.LoadManifest();
        vm.BuildPlan();
        vm.ApproveCommand.Execute(null);
        await vm.RunAsync();

        Assert.Equal(1, store.SaveCount);
        RestoreState saved = Assert.IsType<RestoreState>(store.LastSaved);
        // Both entries recorded Done (recording adapters never throw) — mapped by action→entry id, not position.
        Assert.Equal(RestoreEntryStatus.Done, saved.StatusOf("git"));
        Assert.Equal(RestoreEntryStatus.Done, saved.StatusOf("claude"));

        Assert.False(vm.IsPreviewApproved); // approval consumed after a run
        Assert.Equal(2, vm.ExecutionResults.Count);
    }

    [Fact]
    public void Corrupt_checkpoint_does_not_build_a_resume_plan_from_empty()
    {
        using var fx = new ExecutorFixture();
        var store = new RecordingStateStore { LoadResultOverride = RestoreStateLoad.Corrupt };
        var vm = BuildVm(fx, store, NoopRunner(), Winget("git", "Git.Git"));
        vm.StateDirectory = Path.Combine(Path.GetTempPath(), "wck-install-vm-corrupt");

        vm.LoadManifest();
        vm.BuildPlan();

        Assert.False(vm.HasPlan);
        Assert.NotEmpty(vm.CheckpointWarning);
        Assert.Empty(vm.PlanRows);
        Assert.False(vm.CanResume);
    }

    [Fact]
    public async Task Failed_checkpoint_read_after_run_does_not_overwrite_history()
    {
        using var fx = new ExecutorFixture();
        var store = new RecordingStateStore();
        var vm = BuildVm(fx, store, NoopRunner(), Winget("git", "Git.Git"));
        vm.StateDirectory = Path.Combine(Path.GetTempPath(), "wck-install-vm-unavailable");

        vm.LoadManifest();
        vm.BuildPlan();
        vm.ApproveCommand.Execute(null);
        await vm.RunAsync();
        Assert.Equal(1, store.SaveCount);

        store.LoadResultOverride = RestoreStateLoad.Missing;
        vm.BuildPlan();
        vm.ApproveCommand.Execute(null);
        store.LoadResultOverride = RestoreStateLoad.Unavailable;
        await vm.RunAsync();

        Assert.Equal(1, store.SaveCount);
        Assert.NotEmpty(vm.CheckpointWarning);
    }

    [Fact]
    public void Missing_checkpoint_follows_the_normal_first_run_path()
    {
        using var fx = new ExecutorFixture();
        var store = new RecordingStateStore { LoadResultOverride = RestoreStateLoad.Missing };
        var vm = BuildVm(fx, store, NoopRunner(), Winget("git", "Git.Git"));
        vm.StateDirectory = Path.Combine(Path.GetTempPath(), "wck-install-vm-missing");

        vm.LoadManifest();
        vm.BuildPlan();

        Assert.True(vm.HasPlan);
        Assert.Single(vm.PlanRows);
        Assert.Empty(vm.CheckpointWarning);
    }

    // ---- host-safe export (read-plan + write-JSON only; never runs winget/npm) ----

    [Fact]
    public void ExportPlan_writes_install_plan_json_into_a_temp_state_directory_through_the_gate()
    {
        using var fx = new ExecutorFixture();
        using var ws = new TempWorkspace("wck-install-export-vm-");
        // The real export ring: real writer (re-gates the payload root) + a deterministic clock. Real production gate
        // (ForCurrentSystem) so %TEMP% is an allowed write target.
        ISafetyGate exportGate = new SafetyGate(ProtectedResources.ForCurrentSystem(), new FakeCanonicalizer());
        var runner = new InstallRunner(new InstallPlanWriter(new SanctionedFileWriter()), new FakeClock(T0));

        var i18n = new I18n();
        var planner = new InstallPlanner(exportGate, new AllNetDriverGuard());
        var vm = new InstallViewModel(i18n, new FakeManifestLoader(Winget("git", "Git.Git")), planner,
            new FakeAuthProbe(), new RecordingStateStore(), exportGate, new GatedPlanExecutor(fx.Executor), runner)
        {
            StateDirectory = ws.Root,
        };

        vm.LoadManifest();
        vm.BuildPlan();
        vm.ExportPlan();

        string written = Path.Combine(ws.Root, InstallPlanFiles.Plan);
        Assert.True(File.Exists(written));                       // the JSON landed under the temp root
        string json = File.ReadAllText(written);
        Assert.Contains("\"entryId\": \"git\"", json);
        Assert.Contains("\"wingetId\": \"Git.Git\"", json);
        // Export is read-only: NOTHING reached the destructive executor's adapters.
        Assert.Empty(fx.Adapters.Calls);
    }

    [Fact]
    public void ExportPlan_to_a_refused_target_writes_nothing_and_surfaces_the_refused_summary()
    {
        using var fx = new ExecutorFixture();
        ISafetyGate exportGate = new SafetyGate(ProtectedResources.ForCurrentSystem(), new FakeCanonicalizer());
        var runner = new InstallRunner(new InstallPlanWriter(new SanctionedFileWriter()), new FakeClock(T0));

        var i18n = new I18n();
        var planner = new InstallPlanner(exportGate, new AllNetDriverGuard());
        // A protected/system write target — the writer re-gates it and refuses.
        string refusedTarget = Path.Combine(@"C:\Windows", "wck-install-refused-" + Guid.NewGuid().ToString("N"));
        var vm = new InstallViewModel(i18n, new FakeManifestLoader(Winget("git", "Git.Git")), planner,
            new FakeAuthProbe(), new RecordingStateStore(), exportGate, new GatedPlanExecutor(fx.Executor), runner)
        {
            StateDirectory = refusedTarget,
        };

        vm.LoadManifest();
        vm.BuildPlan();
        vm.ExportPlan();

        Assert.False(Directory.Exists(refusedTarget));          // nothing created under a protected root
        Assert.False(File.Exists(Path.Combine(refusedTarget, InstallPlanFiles.Plan)));
        Assert.Equal(vm.I18n["install.export.refused"], vm.Summary);
        Assert.Empty(fx.Adapters.Calls);                        // and never the destructive executor
    }
}

using System.IO;
using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
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
        public InstallManifest Load(string manifestPath) => new(entries);
        public InstallManifest Parse(string json) => new(entries);
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

        public RestoreState Load(string stateDirectory)
            => _byDir.TryGetValue(stateDirectory, out RestoreState? s) ? s : RestoreState.Empty;

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
    {
        var i18n = new I18n();
        var loader = new FakeManifestLoader(entries);
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

    // ---- no-run-without-approval (the load-bearing non-vacuous proof) ----

    [Fact]
    public void Run_without_approval_records_zero_dispatches_and_writes_no_checkpoint()
    {
        using var fx = new ExecutorFixture();
        var store = new RecordingStateStore();
        var vm = BuildVm(fx, store, NoopRunner(), Winget("git", "Git.Git"));
        vm.StateDirectory = Path.Combine(Path.GetTempPath(), "wck-install-vm-" + Guid.NewGuid().ToString("N"));

        vm.LoadManifest();
        vm.BuildPlan();
        Assert.True(vm.HasPlan);

        // Run WITHOUT approval: the early-return guard must keep the plan out of the executor entirely.
        vm.Run();

        Assert.Empty(fx.Adapters.Calls);     // ZERO adapter dispatches — the recording proof (fail-without)
        Assert.Equal(0, store.SaveCount);    // and no checkpoint persisted
        Assert.Empty(vm.ExecutionResults);
    }

    [Fact]
    public void Approve_then_Run_dispatches_exactly_the_previewed_plan_with_its_own_hash()
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
        vm.Run();

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
    public void After_Run_the_checkpoint_maps_each_action_to_its_entry_and_approval_is_consumed()
    {
        using var fx = new ExecutorFixture();
        var store = new RecordingStateStore();
        var vm = BuildVm(fx, store, NoopRunner(), Winget("git", "Git.Git"), Npm("claude", "@anthropic-ai/claude-code", order: 50));
        vm.StateDirectory = Path.Combine(Path.GetTempPath(), "wck-install-vm-" + Guid.NewGuid().ToString("N"));

        vm.LoadManifest();
        vm.BuildPlan();
        vm.ApproveCommand.Execute(null);
        vm.Run();

        Assert.Equal(1, store.SaveCount);
        RestoreState saved = Assert.IsType<RestoreState>(store.LastSaved);
        // Both entries recorded Done (recording adapters never throw) — mapped by action→entry id, not position.
        Assert.Equal(RestoreEntryStatus.Done, saved.StatusOf("git"));
        Assert.Equal(RestoreEntryStatus.Done, saved.StatusOf("claude"));

        Assert.False(vm.IsPreviewApproved); // approval consumed after a run
        Assert.Equal(2, vm.ExecutionResults.Count);
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
        var runner = new InstallRunner(new InstallPlanWriter(), new FakeClock(T0));

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
        var runner = new InstallRunner(new InstallPlanWriter(), new FakeClock(T0));

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

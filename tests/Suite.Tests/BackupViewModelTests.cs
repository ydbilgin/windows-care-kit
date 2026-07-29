using System.IO;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Tests.Execution;
using WindowsCareKit.Tests.TestInfra;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// The Yedekle (Backup) view-model command-flow wiring (host-safe). It drives build-plan → preview → approve →
/// run over a FAKE manifest loader, the REAL <see cref="BackupPlanner"/>, and the REAL <see cref="BackupRunner"/>
/// bridged onto the REAL <see cref="GatedExecutor"/> via <see cref="BackupExecutorAdapter"/> — the SAME single
/// execution path as production — sitting on RECORDING adapters. So "nothing copies without approval" is proven
/// by ZERO recorded copy dispatches before approve and the EXACT previewed copy action + its hash after. The
/// payload root is a temp workspace; the integrity/report writers re-gate it. No real copy lands and no file
/// outside the temp root is touched.
/// </summary>
public sealed class BackupViewModelTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // %TEMP% lives under the real current user's profile, so the production write-target gate allows the payload
    // destination AND the recording executor authorizes it. Both planner and executor share this gate.
    private static SafetyGate RealGate()
        => new(ProtectedResources.ForCurrentSystem(), new Win32PathCanonicalizer());

    private static async Task PumpAsync(Func<bool> until, int timeoutMs = 30_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10);
        Assert.True(until(), "operation did not complete in time");
    }

    private sealed class FakeManifestLoader(params BackupEntry[] entries) : IManifestLoader
    {
        public BackupManifestLoadResult LoadFromDirectory(string manifestsDirectory)
            => BackupManifestLoadResult.Complete(new BackupManifest(entries));
        public BackupManifestLoadResult LoadFromJson(IEnumerable<string> jsonDocuments)
            => BackupManifestLoadResult.Complete(new BackupManifest(entries));
    }

    private sealed class ResultManifestLoader(BackupManifestLoadResult result) : IManifestLoader
    {
        public BackupManifestLoadResult LoadFromDirectory(string manifestsDirectory) => result;
        public BackupManifestLoadResult LoadFromJson(IEnumerable<string> jsonDocuments) => result;
    }

    private static BackupEntry CopyEntry(string id, string source, string target)
        => new(id, true, BackupMethod.Copy, "cat", source, target,
            Array.Empty<string>(), SecretHandling.Normal, 50, "merge-after-install", $"desc {id}", null);

    [Theory]
    [InlineData("en", "2 to copy · 1 manual · 1 skipped", "1 copied · 1 manual · 1 skipped")]
    [InlineData("tr", "2 kopyalanacak · 1 elle · 1 atlandı", "1 kopyalandı · 1 elle · 1 atlandı")]
    public async Task Preview_and_mixed_run_summaries_distinguish_plan_from_actual_outcomes(
        string culture, string expectedPreview, string expectedResult)
    {
        using var ws = new TempWorkspace("wck-backup-summary-");
        SafetyGate gate = RealGate();
        string sourceRoot = Path.Combine(Path.GetTempPath(), "wck-backup-summary-src");
        string copiedDestination = Path.Combine(ws.Root, "cat", "copied.cfg");
        var fakeFileSystem = new FakeFileSystem().AddFile(copiedDestination, "synthetic settings");
        var runner = new BackupRunner(
            new MixedOutcomeBackupExecutor(),
            new BackupIntegrityWriter(new SanctionedFileWriter()),
            new BackupReportWriter(new LogRedactor(null, null), new SanctionedFileWriter()),
            gate,
            fakeFileSystem,
            new FakeHasher(),
            new FakeClock(T0));
        var vm = new BackupViewModel(
            TestI18n.Full(culture),
            new FakeManifestLoader(
                CopyEntry("copied", Path.Combine(sourceRoot, "copied.cfg"), "cat/copied.cfg"),
                CopyEntry("failed", Path.Combine(sourceRoot, "failed.cfg"), "cat/failed.cfg"),
                new BackupEntry("manual", true, BackupMethod.Copy, "cat", "secret.db", "cat/secret.db",
                    Array.Empty<string>(), SecretHandling.NeverRead, 50, "manual", "manual", null),
                new BackupEntry("disabled", false, BackupMethod.Copy, "cat", "cache", "cat/cache",
                    Array.Empty<string>(), SecretHandling.Normal, 50, "skip", "disabled", null)),
            new BackupPlanner(gate, new Win32EnvironmentExpander(), TestData.PayloadRoots()),
            runner)
        {
            PayloadDir = ws.Root,
        };

        await vm.BuildPlanAsync();
        Assert.Equal(expectedPreview, vm.Summary);

        vm.IsPreviewApproved = true;
        await vm.RunAsync();

        Assert.Equal(expectedResult, vm.Summary);
        Assert.Equal(1, vm.ResultRows.Count(row => row.RiskText == "COPIED"));
        Assert.Equal(1, vm.ResultRows.Count(row => row.RiskText == "SKIPPED"));
    }

    /// <summary>The VM over the real planner + the real runner bridged onto the recording GatedExecutor.</summary>
    private static BackupViewModel BuildVm(ExecutorFixture fx, TempWorkspace ws, params BackupEntry[] entries)
        => BuildVm(fx, ws, new FakeManifestLoader(entries), new I18n());

    private static BackupViewModel BuildVm(
        ExecutorFixture fx,
        TempWorkspace ws,
        IManifestLoader manifestLoader,
        I18n i18n,
        PayloadRootPolicy? payloadRoots = null)
    {
        var planner = new BackupPlanner(
            fx.Gate,
            new Win32EnvironmentExpander(),
            payloadRoots ?? TestData.PayloadRoots());
        var runner = new BackupRunner(
            new BackupExecutorAdapter(fx.Executor),
            new BackupIntegrityWriter(new SanctionedFileWriter()),
            new BackupReportWriter(new LogRedactor(null, null), new SanctionedFileWriter()),
            fx.Gate,
            new PhysicalFileSystem(),
            new Sha256Hasher(),
            new FakeClock(T0));
        return new BackupViewModel(i18n, manifestLoader, planner, runner)
        {
            PayloadDir = ws.Root,
        };
    }

    [Fact]
    public async Task Payload_inside_the_forbidden_root_surfaces_the_outside_app_warning()
    {
        using var fx = new ExecutorFixture(RealGate());
        using var ws = new TempWorkspace("wck-backup-forbidden-payload-");
        I18n i18n = TestI18n.Full();
        string source = Path.Combine(Path.GetTempPath(), "wck-backup-src", "App");
        BackupViewModel vm = BuildVm(
            fx,
            ws,
            new FakeManifestLoader(CopyEntry("docs", source, "cat/App")),
            i18n,
            TestData.PayloadRoots(ws.Root));

        await vm.BuildPlanAsync();

        // Not IsNullOrWhiteSpace: I18n returns the key itself on a miss, so that can never fail and
        // would only look like a "the user sees real localized text" proof.
        Assert.NotEqual("backup.payloadOutsideRepo", vm.PayloadWarning);
        Assert.Equal(i18n["backup.payloadOutsideRepo"], vm.PayloadWarning);
    }

    [Fact]
    public async Task Manifest_outcomes_reach_distinct_visible_state()
    {
        using var fx = new ExecutorFixture(RealGate());
        using var ws = new TempWorkspace("wck-backup-manifest-health-");
        BackupEntry entry = CopyEntry("docs", Path.Combine(Path.GetTempPath(), "source", "settings.json"), "cat/settings.json");
        var cases = new[]
        {
            new BackupManifestLoadResult(new BackupManifest([]), BackupManifestLoadStatus.NotInstalled, []),
            BackupManifestLoadResult.Complete(new BackupManifest([entry])),
            BackupManifestLoadResult.Complete(new BackupManifest([])),
            new BackupManifestLoadResult(new BackupManifest([]), BackupManifestLoadStatus.Unavailable,
                [new(@"C:\app\manifests\bad.json", BackupManifestFileStatus.Malformed, "JsonException")]),
            new BackupManifestLoadResult(new BackupManifest([]), BackupManifestLoadStatus.Unavailable,
                [new(@"C:\app\manifests\locked.json", BackupManifestFileStatus.Unreadable, "IOException")]),
        };

        var visibleStates = new HashSet<(string Summary, string Info, string Health)>();
        foreach (BackupManifestLoadResult load in cases)
        {
            BackupViewModel vm = BuildVm(
                fx, ws, new ResultManifestLoader(load), TestI18n.Full());
            await vm.BuildPlanAsync();
            visibleStates.Add((vm.Summary, vm.ManifestInfoNote, vm.ManifestHealthNote));
        }

        Assert.Equal(5, visibleStates.Count);
        Assert.Contains(visibleStates, s => s.Info.Contains("not installed") && s.Health.Length == 0);
        Assert.Contains(visibleStates, s => s.Summary.StartsWith("1 to copy") && s.Info.Length == 0 && s.Health.Length == 0);
        Assert.Contains(visibleStates, s => s.Summary.StartsWith("0 to copy") && s.Info.Length == 0 && s.Health.Length == 0);
        // MAJOR-02: both cases are Unavailable, so the sentence must be the HARD one ("could not be
        // determined"), never the softer Partial wording ("may be incomplete"). Collapsing the VM's
        // status->key choice to always-Partial is otherwise a silent, materially false downgrade.
        // MINOR-03: the localized cause clause must distinguish corrupt from unreadable.
        Assert.Contains(visibleStates, s => s.Health.Contains(@"C:\app\manifests\bad.json") && s.Health.Contains("JsonException")
            && s.Health.Contains("could not be determined") && s.Health.Contains("(corrupt, "));
        Assert.Contains(visibleStates, s => s.Health.Contains(@"C:\app\manifests\locked.json") && s.Health.Contains("IOException")
            && s.Health.Contains("could not be determined") && s.Health.Contains("(unreadable, "));
    }

    [Fact]
    public async Task Partial_manifest_inventory_keeps_good_entries_and_surfaces_incomplete_health()
    {
        using var fx = new ExecutorFixture(RealGate());
        using var ws = new TempWorkspace("wck-backup-manifest-partial-vm-");
        BackupEntry entry = CopyEntry("docs", Path.Combine(Path.GetTempPath(), "source", "settings.json"), "cat/settings.json");
        var load = new BackupManifestLoadResult(
            new BackupManifest([entry]),
            BackupManifestLoadStatus.Partial,
            [
                new(@"C:\app\manifests\good.json", BackupManifestFileStatus.Loaded, null),
                new(@"C:\app\manifests\bad.json", BackupManifestFileStatus.Malformed, "JsonException"),
            ]);
        BackupViewModel vm = BuildVm(fx, ws, new ResultManifestLoader(load), TestI18n.Full());

        await vm.BuildPlanAsync();

        Assert.Single(vm.PlanRows);
        Assert.Contains("may be incomplete", vm.ManifestHealthNote);
        Assert.Contains(@"C:\app\manifests\bad.json", vm.ManifestHealthNote);
    }

    // ---- build-plan → preview → approve gating ----

    [Fact]
    public async Task BuildPlan_produces_a_preview_and_run_is_disabled_until_approved()
    {
        using var fx = new ExecutorFixture(RealGate());
        using var ws = new TempWorkspace("wck-backup-vm-");
        // A copy source under the real user profile (gate evaluates the DESTINATION, which is under the temp root).
        string source = Path.Combine(Path.GetTempPath(), "wck-backup-src", "App");
        var vm = BuildVm(fx, ws, CopyEntry("docs", source, "cat/App"));

        await vm.BuildPlanAsync();

        Assert.True(vm.HasPlan);
        Assert.Single(vm.PlanRows);               // the copy became one previewed row
        Assert.False(vm.CanRun);                  // a plan exists but is not yet approved → run disabled
        Assert.Empty(fx.Adapters.Calls);          // building the plan copies NOTHING
    }

    // ---- no-run-without-approval (the load-bearing non-vacuous proof) ----

    [Fact]
    public async Task Run_without_approval_records_zero_copy_dispatches()
    {
        using var fx = new ExecutorFixture(RealGate());
        using var ws = new TempWorkspace("wck-backup-vm-");
        string source = Path.Combine(Path.GetTempPath(), "wck-backup-src", "App");
        var vm = BuildVm(fx, ws, CopyEntry("docs", source, "cat/App"));

        await vm.BuildPlanAsync();
        Assert.True(vm.HasPlan);
        Assert.False(vm.IsPreviewApproved);

        // Run WITHOUT approval: the CanRun guard must keep the plan out of the executor entirely.
        await vm.RunAsync();

        Assert.Empty(fx.Adapters.Calls);          // ZERO copy dispatches — the recording proof (fail-without)
        Assert.False(vm.HasResults);
    }

    [Fact]
    public async Task Approve_then_Run_dispatches_exactly_the_previewed_copy_with_its_own_hash()
    {
        using var fx = new ExecutorFixture(RealGate());
        using var ws = new TempWorkspace("wck-backup-vm-");
        string source = Path.Combine(Path.GetTempPath(), "wck-backup-src", "App");
        var vm = BuildVm(fx, ws, CopyEntry("docs", source, "cat/App"));

        await vm.BuildPlanAsync();
        Assert.Empty(fx.Adapters.Dispatched.OfType<CopyAction>()); // baseline: nothing dispatched yet

        vm.IsPreviewApproved = true;
        Assert.True(vm.CanRun);
        await vm.RunAsync();
        await PumpAsync(() => vm.HasResults);

        // The recording copy adapter received EXACTLY the previewed copy action (non-vacuous: a regression that
        // bypassed the executor or copied a different target would change this recorded source/destination).
        CopyAction ran = Assert.Single(fx.Adapters.Dispatched.OfType<CopyAction>());
        Assert.Equal(source, ran.Source);
        Assert.Equal(Path.GetFullPath(Path.Combine(ws.Root, "cat", "App")), ran.Destination);

        // TOCTOU: the hash the executor authorized against is the previewed plan's ComputeHash().
        var rebuilt = new BackupPlanner(fx.Gate, new Win32EnvironmentExpander(), TestData.PayloadRoots())
            .BuildPlan(new BackupManifest(new[] { CopyEntry("docs", source, "cat/App") }), ws.Root, T0).Plan;
        Assert.Equal(rebuilt.ComputeHash(), LoggedPlanHash(fx));

        // The copy is marked COPIED in the result rows.
        Assert.Single(vm.ResultRows);
        Assert.Equal("COPIED", vm.ResultRows[0].RiskText);
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

    private sealed class MixedOutcomeBackupExecutor : IBackupExecutor
    {
        public BackupExecutionReport Execute(OperationPlan plan, string approvedPlanHash)
        {
            CopyAction[] actions = plan.Actions.OfType<CopyAction>().ToArray();
            Assert.Equal(2, actions.Length);
            return new BackupExecutionReport(true,
            [
                new BackupActionResult(actions[0].Id, BackupActionStatus.Done, "done"),
                new BackupActionResult(actions[1].Id, BackupActionStatus.Failed, "IOException: synthetic failure"),
            ]);
        }
    }

    // ---- changing the payload resets the approval (no stale approve survives a re-target) ----

    [Fact]
    public async Task Changing_the_payload_dir_resets_the_plan_and_approval()
    {
        using var fx = new ExecutorFixture(RealGate());
        using var ws = new TempWorkspace("wck-backup-vm-");
        string source = Path.Combine(Path.GetTempPath(), "wck-backup-src", "App");
        var vm = BuildVm(fx, ws, CopyEntry("docs", source, "cat/App"));

        await vm.BuildPlanAsync();
        vm.IsPreviewApproved = true;
        Assert.True(vm.CanRun);

        // Re-target the payload → the prior plan + approval must be discarded (no stale-approval run path).
        vm.PayloadDir = Path.Combine(Path.GetTempPath(), "wck-backup-other-" + Guid.NewGuid().ToString("N"));

        Assert.False(vm.HasPlan);
        Assert.False(vm.IsPreviewApproved);
        Assert.False(vm.CanRun);
        Assert.Empty(vm.PlanRows);
    }

    [Fact]
    public void Payload_dir_cannot_change_during_an_in_flight_operation()
    {
        using var fx = new ExecutorFixture(RealGate());
        using var ws = new TempWorkspace("wck-backup-vm-busy-");
        var vm = BuildVm(fx, ws);
        string payloadDir = vm.PayloadDir;
        SetBusy(vm, true);

        Assert.False(vm.CanEditDirectories);
        vm.PayloadDir = Path.Combine(Path.GetTempPath(), "wck-backup-other-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(payloadDir, vm.PayloadDir);
    }

    private static void SetBusy(BackupViewModel vm, bool value)
    {
        System.Reflection.FieldInfo field = typeof(BackupViewModel).GetField(
            "_isBusy",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(vm, value);
    }
}

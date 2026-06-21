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
        public BackupManifest LoadFromDirectory(string manifestsDirectory) => new(entries);
        public BackupManifest LoadFromJson(IEnumerable<string> jsonDocuments) => new(entries);
    }

    private static BackupEntry CopyEntry(string id, string source, string target)
        => new(id, true, BackupMethod.Copy, "cat", source, target,
            Array.Empty<string>(), SecretHandling.Normal, 50, "merge-after-install", $"desc {id}", null);

    /// <summary>The VM over the real planner + the real runner bridged onto the recording GatedExecutor.</summary>
    private static BackupViewModel BuildVm(ExecutorFixture fx, TempWorkspace ws, params BackupEntry[] entries)
    {
        var i18n = new I18n();
        var planner = new BackupPlanner(fx.Gate, new Win32EnvironmentExpander());
        var runner = new BackupRunner(
            new BackupExecutorAdapter(fx.Executor),
            new BackupIntegrityWriter(),
            new BackupReportWriter(new LogRedactor(null, null)),
            fx.Gate,
            new PhysicalFileSystem(),
            new Sha256Hasher(),
            new FakeClock(T0));
        return new BackupViewModel(i18n, new FakeManifestLoader(entries), planner, runner)
        {
            PayloadDir = ws.Root,
        };
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
        var rebuilt = new BackupPlanner(fx.Gate, new Win32EnvironmentExpander())
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
}

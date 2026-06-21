using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Tests.Execution;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>Fail-closed: a per-action block/throw stops the plan; the Low+Full junk carve-out continues (§A.4).</summary>
public class GatedExecutorFailClosedTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static FileDeleteAction Junk(string path) => new()
    {
        Path = path,
        ToRecycleBin = true,
        Description = "junk " + path,
        Reason = "temp",
        Risk = RiskLevel.Low,
        Undo = UndoCapability.Full,
        BestEffort = true, // junk sweep flag — the only thing that makes a delete continue-on-failure (L2)
    };

    [Fact]
    public void A_per_action_exception_stops_the_plan_and_marks_the_rest_NotRun()
    {
        using var fx = new ExecutorFixture();

        // A High/None service-delete is NOT best-effort → its failure must stop the plan.
        var first = TestData.FileDelete(@"C:\Program Files\SomeApp\a.tmp");
        var failing = TestData.Service("SomeVendorSvc", ServiceOperation.Delete);
        var third = TestData.FileDelete(@"C:\Program Files\SomeApp\c.tmp");

        fx.Adapters.ThrowForActionIds.Add(failing.Id);

        var plan = new OperationPlan("t", "uninstall",
            new PlannedAction[] { first, failing, third }, T0);

        var report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.True(report.Authorized);
        Assert.Equal(ActionStatus.Done, report.Results[0].Status);
        Assert.Equal(ActionStatus.Failed, report.Results[1].Status);
        Assert.Equal(ActionStatus.NotRun, report.Results[2].Status);
        Assert.False(report.AllDone);
        // third must never have reached its adapter
        Assert.DoesNotContain($"file:{third.Id}", fx.Adapters.Calls);
    }

    [Fact]
    public void Best_effort_low_full_file_delete_continues_on_failure_and_tallies()
    {
        using var fx = new ExecutorFixture();

        var a = Junk(@"C:\Program Files\SomeApp\a.tmp");
        var lockedB = Junk(@"C:\Program Files\SomeApp\b.tmp");
        var c = Junk(@"C:\Program Files\SomeApp\c.tmp");

        fx.Adapters.ThrowForActionIds.Add(lockedB.Id); // one locked temp file

        var plan = new OperationPlan("clean", "clean", new PlannedAction[] { a, lockedB, c }, T0);
        var report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.True(report.Authorized);
        Assert.Equal(ActionStatus.Done, report.Results[0].Status);
        Assert.Equal(ActionStatus.Failed, report.Results[1].Status);
        Assert.Equal(ActionStatus.Done, report.Results[2].Status); // continued past the locked one
        Assert.Equal(2, report.DoneCount);
        Assert.Equal(1, report.FailedCount);
        // all three adapters were attempted
        Assert.Contains($"file:{c.Id}", fx.Adapters.Calls);
    }

    [Fact]
    public void A_low_full_delete_without_the_best_effort_flag_fails_closed()
    {
        using var fx = new ExecutorFixture();

        // An uninstall leftover folder delete is Low+Full (recycle bin) but is NOT a junk-sweep action,
        // so BestEffort is false → its failure must STOP the plan (L2: carve-out is flag-keyed, not tier-keyed).
        var leftover = new FileDeleteAction
        {
            Path = @"C:\Program Files\SomeApp\leftover",
            ToRecycleBin = true,
            Description = "leftover",
            Reason = "uninstall remnant",
            Risk = RiskLevel.Low,
            Undo = UndoCapability.Full,
            // BestEffort deliberately left false
        };
        var after = TestData.FileDelete(@"C:\Program Files\SomeApp\after.tmp");

        fx.Adapters.ThrowForActionIds.Add(leftover.Id);

        var plan = new OperationPlan("t", "uninstall", new PlannedAction[] { leftover, after }, T0);
        var report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.True(report.Authorized);
        Assert.Equal(ActionStatus.Failed, report.Results[0].Status);
        Assert.Equal(ActionStatus.NotRun, report.Results[1].Status); // plan stopped — not best-effort
        Assert.DoesNotContain($"file:{after.Id}", fx.Adapters.Calls);
    }

    /// <summary>
    /// The action kinds the TOCTOU-flip test exercises. Item 1(c): the flipped/blocked middle action is not
    /// only a FileDelete but ALSO a ServiceDeleteAction and a CommandAction — so the per-action re-gate is
    /// proven to fail closed (Blocked + adapter NOT called) for the service: and command: dispatch paths too.
    /// </summary>
    public enum FlipKind { File, Service, Command }

    private static (PlannedAction action, string callPrefix) FlipTarget(FlipKind kind) => kind switch
    {
        FlipKind.File => (TestData.FileDelete(@"C:\Program Files\SomeApp\b.tmp"), "file"),
        FlipKind.Service => (TestData.Service("SomeVendorSvc", ServiceOperation.Delete), "service"),
        FlipKind.Command => (TestData.Command(@"C:\Program Files\SomeApp\uninst.exe", "/S"), "command"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [InlineData(FlipKind.File)]
    [InlineData(FlipKind.Service)]
    [InlineData(FlipKind.Command)]
    public void A_gate_block_at_execution_time_stops_the_plan_TOCTOU(FlipKind kind)
    {
        // The gate allows everything at Validate (authorization), but flips one action to blocked at the
        // per-action Evaluate that the executor does right before touching the OS.
        var first = TestData.FileDelete(@"C:\Program Files\SomeApp\a.tmp");
        (PlannedAction becomesBlocked, string callPrefix) = FlipTarget(kind);
        var third = TestData.FileDelete(@"C:\Program Files\SomeApp\c.tmp");

        var gate = new FlipAtExecutionGate(blockActionId: becomesBlocked.Id);

        string logPath = Path.Combine(Path.GetTempPath(), "wck-toctou-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var adapters = new RecordingAdapters();
        var executor = new GatedExecutor(
            gate, new ExecutionLog(logPath, new LogRedactor(null, null)),
            adapters.File, adapters.Registry, adapters.Service,
            adapters.Task, adapters.Process, adapters.Copy);

        try
        {
            var plan = new OperationPlan("t", "uninstall",
                new PlannedAction[] { first, becomesBlocked, third }, T0);

            var report = executor.ExecuteWithReport(plan, plan.ComputeHash());

            Assert.True(report.Authorized);
            Assert.Equal(ActionStatus.Done, report.Results[0].Status);
            Assert.Equal(ActionStatus.Blocked, report.Results[1].Status);
            Assert.Equal(ActionStatus.NotRun, report.Results[2].Status);
            // GUARDRAIL (adapter-not-called): the blocked action's matching adapter (file/service/command)
            // was NEVER invoked — the re-gate stops it BEFORE dispatch, for every kind.
            Assert.DoesNotContain($"{callPrefix}:{becomesBlocked.Id}", adapters.Calls);
            // And the third action never ran either (plan stopped at the block).
            Assert.DoesNotContain($"file:{third.Id}", adapters.Calls);
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }

    // Item 5: Co-staged restore-point fail-closed. A protective CreateRestorePointAction is PREPENDED to a
    // destructive action. The real Win32RestorePointCreator, given an UNAVAILABLE capability probe, throws
    // BEFORE any Win32 call → the restore point Fails → the plan stops → the destructive neighbor is NotRun
    // and its adapter is NEVER dispatched. This proves the user-opted safety net being absent halts the
    // destruction (it does not proceed without the promised rollback layer).
    [Theory]
    [InlineData(DestructiveKind.Command)]
    [InlineData(DestructiveKind.FileDelete)]
    public void An_unavailable_restore_point_fails_closed_and_the_destructive_neighbor_never_runs(DestructiveKind kind)
    {
        var restorePoint = new CreateRestorePointAction
        {
            RestorePointName = "Windows Care Kit — before uninstall",
            Description = "create restore point",
            Reason = "protective rollback layer",
            Risk = RiskLevel.Info,
            Undo = UndoCapability.None,
        };
        (PlannedAction destructive, string callPrefix) = DestructiveNeighbor(kind);

        // The real creator over an UNAVAILABLE probe → throws (honest failure) when dispatched.
        var creator = new Win32RestorePointCreator(new FakeCapability(available: false));

        string logPath = Path.Combine(Path.GetTempPath(), "wck-rpfc-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var adapters = new RecordingAdapters();
        var executor = new GatedExecutor(
            TestData.Gate(), new ExecutionLog(logPath, new LogRedactor(null, null)),
            adapters.File, adapters.Registry, adapters.Service,
            adapters.Task, adapters.Process, adapters.Copy, creator);

        try
        {
            var plan = new OperationPlan("t", "uninstall",
                new PlannedAction[] { restorePoint, destructive }, T0);

            var report = executor.ExecuteWithReport(plan, plan.ComputeHash());

            Assert.True(report.Authorized);                              // the gate allows it (pure system call)
            Assert.Equal(ActionStatus.Failed, report.Results[0].Status); // restore point failed closed (SR off)
            Assert.Equal(ActionStatus.NotRun, report.Results[1].Status); // destructive neighbor never started
            // GUARDRAIL (adapter-not-called): the destructive adapter was NEVER dispatched.
            Assert.DoesNotContain($"{callPrefix}:{destructive.Id}", adapters.Calls);
            Assert.Empty(adapters.Dispatched); // nothing destructive was dispatched at all
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }

    public enum DestructiveKind { Command, FileDelete }

    private static (PlannedAction action, string callPrefix) DestructiveNeighbor(DestructiveKind kind) => kind switch
    {
        // An official-uninstaller-shaped command with Undo=None (the canonical Irreversible destructive neighbor).
        DestructiveKind.Command => (new CommandAction
        {
            FileName = @"C:\Program Files\SomeApp\uninst.exe",
            Arguments = new[] { "/S" },
            Description = "run the official uninstaller",
            Reason = "vendor uninstaller",
            Risk = RiskLevel.Medium,
            Undo = UndoCapability.None,
        }, "command"),
        DestructiveKind.FileDelete => (TestData.FileDelete(@"C:\Program Files\SomeApp\leftover"), "file"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class FakeCapability(bool available) : IRestorePointCapabilityProbe
    {
        public bool IsAvailable() => available;
    }

    /// <summary>Allows the whole plan at <see cref="Validate"/>, but blocks one action id at per-action <see cref="Evaluate"/>.</summary>
    private sealed class FlipAtExecutionGate : ISafetyGate
    {
        private readonly string _blockId;
        private bool _validating;

        public FlipAtExecutionGate(string blockActionId) => _blockId = blockActionId;

        public SafetyVerdict Evaluate(PlannedAction action)
        {
            // During Validate (authorization), allow everything so authorization passes.
            if (_validating)
                return SafetyVerdict.Allow();
            // During the executor's per-action re-check, block the target action.
            return action.Id == _blockId
                ? SafetyVerdict.Block("world changed since approval")
                : SafetyVerdict.Allow();
        }

        public PlanValidationResult Validate(OperationPlan plan)
        {
            _validating = true;
            try
            {
                var results = plan.Actions.Select(a => new ActionVerdict(a, Evaluate(a))).ToArray();
                return new PlanValidationResult(results.All(r => r.Verdict.Allowed), results);
            }
            finally
            {
                _validating = false;
            }
        }
    }
}

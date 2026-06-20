using System.Reflection;
using WindowsCareKit.App.ViewModels;
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

/// <summary>
/// PR-5 (the most safety-sensitive slice) — the protective <see cref="CreateRestorePointAction"/> + the
/// non-escalating tier exemption + the SafetyGate Allow arm + the executor Dispatch routing + the capability
/// probe logic + wizard co-staging. ALL host-safe (fakes; nothing creates a real restore point). The genuine
/// "create a real restore point" proof is the separate <c>[DisposableFact]</c> in
/// <c>RestorePointDisposableTests</c> (host-SKIPPED).
/// </summary>
public class RestorePointTests
{
    private static readonly DateTime T0 = new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

    private static CreateRestorePointAction RestorePoint() => new()
    {
        RestorePointName = "Windows Care Kit — before uninstalling SomeApp",
        Description = "Create a System Restore point before uninstalling SomeApp",
        Reason = "Protective rollback layer",
        Risk = RiskLevel.Info,
        Undo = UndoCapability.None,
        // IsProtective is type-bound true on CreateRestorePointAction (PR-5 FIX 1) — never a settable flag.
    };

    /// <summary>
    /// A genuinely destructive, NON-protective action that shares the restore point's Undo=None shape: a
    /// registry key delete with no undo. Used where a test needs a non-protective Undo=None action (the
    /// IsProtective flag is no longer settable, so the old <c>with { IsProtective = false }</c> trick is gone).
    /// </summary>
    private static RegistryDeleteAction DestructiveUndoNone() => new()
    {
        Hive = RegistryHive.LocalMachine,
        SubKeyPath = @"SOFTWARE\SomeVendor\SomeApp",
        Description = "delete a registry key with no undo",
        Reason = "leftover key",
        Risk = RiskLevel.Medium,
        Undo = UndoCapability.None,
    };

    /// <summary>An official-uninstaller-shaped destructive neighbor: a command with Undo=None.</summary>
    private static CommandAction OfficialUninstaller() => new()
    {
        FileName = @"C:\Program Files\SomeApp\uninst.exe",
        Arguments = new[] { "/S" },
        Description = "Run the official uninstaller for SomeApp",
        Reason = "vendor uninstaller",
        Risk = RiskLevel.Medium,
        Undo = UndoCapability.None,
    };

    // ============================================================ 1) The typed action ============

    [Fact]
    public void CreateRestorePointAction_is_protective_info_risk_and_keeps_undo_none()
    {
        var a = RestorePoint();
        Assert.True(a.IsProtective);                  // the explicit non-escalating marker
        Assert.Equal(RiskLevel.Info, a.Risk);         // Info/Low — never escalating on its own
        Assert.Equal(UndoCapability.None, a.Undo);    // NOT relabeled to dodge the tier (would break MigrationRestoreConfirmFlowTests:66)
        Assert.Equal("restore.create", a.Kind);
    }

    [Fact]
    public void Protective_marker_is_excluded_from_the_target_signature_and_plan_hash()
    {
        // IsProtective is now TYPE-BOUND (always true on CreateRestorePointAction, not settable), so it can no
        // longer be flipped to prove exclusion directly. Instead prove it is INVISIBLE to both the target
        // signature and the plan hash: a protective restore point and a NON-protective action that share the
        // restore point's target signature + risk + undo hash the SAME — the protective marker adds nothing to
        // the hashed identity (it is tier metadata only, UI decision §5). The differentiator is solely the
        // signature/risk/undo, never IsProtective. (ComputeHash mixes in risk+undo, so they are held equal.)
        var rp = RestorePoint();                                      // IsProtective = true (type-bound)
        var twin = new SignatureTwinForRestorePoint                   // IsProtective = false (default)
        {
            Description = rp.Description, Reason = rp.Reason, Risk = rp.Risk, Undo = rp.Undo,
        };

        Assert.True(rp.IsProtective);
        Assert.False(twin.IsProtective);
        Assert.Equal(rp.TargetSignature(), twin.TargetSignature());   // same WHAT — protective flag absent from it

        var p1 = new OperationPlan("t", "uninstall", new PlannedAction[] { rp }, T0);
        var p2 = new OperationPlan("t", "uninstall", new PlannedAction[] { twin }, T0);
        Assert.Equal(p1.ComputeHash(), p2.ComputeHash());            // identical hash → IsProtective not hashed
    }

    /// <summary>
    /// A non-protective action whose target signature is byte-identical to <see cref="RestorePoint"/>'s — the
    /// only difference between the two is the type-bound IsProtective. Used to prove the protective marker is
    /// excluded from the signature AND the plan hash (it can no longer be flipped on one instance).
    /// </summary>
    private sealed record SignatureTwinForRestorePoint : PlannedAction
    {
        public override string Kind => "restore.create";
        public override string TargetSignature()
            => $"{Kind}|{"Windows Care Kit — before uninstalling SomeApp".ToLowerInvariant()}";
    }

    // ============================================================ 2) TierFor exemption ===========

    [Fact]
    public void TierFor_a_lone_restore_point_is_NOT_irreversible()
    {
        // PASS-WITH the exemption: a protective-only plan must not escalate (the exemption working).
        var plan = new OperationPlan("rp", "uninstall", new PlannedAction[] { RestorePoint() }, T0);
        Assert.NotEqual(ConfirmTier.Irreversible, ConfirmGateViewModel.TierFor(plan));
    }

    [Fact]
    public void TierFor_restore_point_plus_official_uninstaller_IS_irreversible()
    {
        // The destructive neighbor (Undo=None) drives Irreversible — exactly the cx goal preserved (UI §5).
        var plan = new OperationPlan("rp+official", "uninstall",
            new PlannedAction[] { RestorePoint(), OfficialUninstaller() }, T0);
        Assert.Equal(ConfirmTier.Irreversible, ConfirmGateViewModel.TierFor(plan));
    }

    [Fact]
    public void TierFor_FAIL_WITHOUT_exemption_a_lone_undoNone_action_WOULD_be_irreversible()
    {
        // FAIL-WITHOUT proof: a non-protective action that SHARES the restore point's Undo=None shape DOES
        // escalate to Irreversible. This isolates the type-bound exemption as the thing that keeps the protective
        // restore point from escalating — not its Undo/Risk (this destructive action shares Undo=None with it).
        var notProtective = DestructiveUndoNone();
        Assert.False(notProtective.IsProtective);
        var plan = new OperationPlan("destructive-noflag", "uninstall", new PlannedAction[] { notProtective }, T0);
        Assert.Equal(ConfirmTier.Irreversible, ConfirmGateViewModel.TierFor(plan));
    }

    [Fact]
    public void TierFor_restore_point_plus_partial_undo_neighbor_is_Medium_not_escalated()
    {
        // A protective restore point alongside a Medium/Partial registry delete → Medium (driven by the
        // neighbor), never bumped by the restore point.
        var regDelete = new RegistryDeleteAction
        {
            Hive = RegistryHive.LocalMachine, SubKeyPath = @"SOFTWARE\SomeVendor\SomeApp",
            Description = "del", Reason = "t", Risk = RiskLevel.Medium, Undo = UndoCapability.Partial,
        };
        var plan = new OperationPlan("rp+reg", "uninstall",
            new PlannedAction[] { RestorePoint(), regDelete }, T0);
        Assert.Equal(ConfirmTier.Medium, ConfirmGateViewModel.TierFor(plan));
    }

    // =================================== 2b) The exemption is TYPE-BOUND, not self-claimable ======
    // PR-5 audit FIX 1: IsProtective used to be a settable init bool on the base record, so a destructive
    // action could set it true and exempt itself from the Irreversible type-to-confirm tier (a gate bypass,
    // also hash-invisible). It is now a type-bound virtual. These tests LOCK that abuse vector shut.

    [Fact]
    public void Destructive_actions_with_undo_none_are_NOT_protective()
    {
        // The exact abuse shapes called out by the audit: a registry delete and a command, both Undo=None.
        // Neither can be protective — the base getter is false and they do not override it.
        var registryDelete = new RegistryDeleteAction
        {
            Hive = RegistryHive.LocalMachine, SubKeyPath = @"SOFTWARE\SomeVendor\SomeApp",
            Description = "del", Reason = "t", Undo = UndoCapability.None,
        };
        var command = new CommandAction
        {
            FileName = @"C:\Program Files\SomeApp\uninst.exe", Arguments = new[] { "/S" },
            Description = "run uninstaller", Reason = "t", Undo = UndoCapability.None,
        };

        Assert.False(registryDelete.IsProtective);
        Assert.False(command.IsProtective);
    }

    [Fact]
    public void TierFor_a_lone_destructive_undoNone_action_canNOT_self_exempt_from_Irreversible()
    {
        // A plan of ONLY a destructive Undo=None action returns Irreversible: there is no settable flag for it
        // to exclude itself from `driving`, so it cannot dodge the type-to-confirm tier (PR-5 FIX 1).
        var plan = new OperationPlan("lone-destructive", "uninstall",
            new PlannedAction[] { DestructiveUndoNone() }, T0);
        Assert.Equal(ConfirmTier.Irreversible, ConfirmGateViewModel.TierFor(plan));
    }

    [Fact]
    public void Only_CreateRestorePointAction_is_protective_among_all_concrete_action_types()
    {
        // STRUCTURAL guard: enumerate every concrete PlannedAction subtype in Core and assert the ONLY one whose
        // IsProtective is true is CreateRestorePointAction. Catches a future destructive subtype that mistakenly
        // overrides IsProtective to true (which would re-open the gate-bypass) — fails at THAT moment, not in prod.
        var concreteTypes = typeof(PlannedAction).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(PlannedAction)))
            .ToArray();

        // Sanity: we actually found the action hierarchy (a non-vacuous enumeration).
        Assert.Contains(typeof(CreateRestorePointAction), concreteTypes);
        Assert.Contains(typeof(RegistryDeleteAction), concreteTypes);

        var protectiveTypes = concreteTypes
            .Where(t => ((PlannedAction)CreateUninitialized(t)).IsProtective)
            .ToArray();

        Assert.Equal(new[] { typeof(CreateRestorePointAction) }, protectiveTypes);
    }

    /// <summary>
    /// Materialize a PlannedAction subtype WITHOUT running its ctor / required-member init — IsProtective is a
    /// computed virtual that depends on no instance state, so an uninitialized instance reports it faithfully.
    /// This lets the structural test cover every type without knowing each one's required properties.
    /// </summary>
    private static object CreateUninitialized(Type t)
        => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);

    [Fact]
    public void A_rogue_action_overriding_IsProtective_true_still_CANNOT_dodge_the_irreversible_tier()
    {
        // STRONGEST abuse proof (cx PR-5 fix-verify hardening): TierFor's exemption is keyed to the EXACT
        // protective TYPE (the closed IsTierExempt predicate), NOT the overridable IsProtective property. A
        // destructive action that wrongly overrides IsProtective => true stays in `driving` → escalates →
        // cannot self-exempt from the type-to-confirm tier, even via a rogue override.
        var rogue = new RogueProtectiveDestructiveAction { Description = "delete everything", Reason = "rogue" };
        Assert.True(rogue.IsProtective);                 // it LIES via the overridable marker...
        Assert.Equal(UndoCapability.None, rogue.Undo);   // ...and is genuinely destructive (no undo)...
        var plan = new OperationPlan("rogue", "uninstall", new PlannedAction[] { rogue }, T0);
        Assert.Equal(ConfirmTier.Irreversible, ConfirmGateViewModel.TierFor(plan)); // ...but TierFor ignores it.
    }

    /// <summary>A test-only DESTRUCTIVE action that maliciously overrides the IsProtective marker to true —
    /// proves TierFor keys off the exact protective TYPE (closed IsTierExempt), not this overridable property.</summary>
    private sealed record RogueProtectiveDestructiveAction : PlannedAction
    {
        public override bool IsProtective => true;
        public override string Kind => "rogue.delete";
        public override string TargetSignature() => $"{Kind}|rogue";
    }

    // ============================================================ 3) SafetyGate Allow arm ========

    [Fact]
    public void SafetyGate_allows_a_create_restore_point_action()
    {
        SafetyVerdict verdict = TestData.Gate().Evaluate(RestorePoint());
        Assert.True(verdict.Allowed);
        Assert.Contains("restore point", verdict.Reason);
    }

    [Fact]
    public void SafetyGate_still_blocks_an_unknown_action_type_after_the_restore_point_arm()
    {
        // No accidental catch-all: an unmodeled type STILL hits the fail-closed `_ => Block` arm.
        var verdict = TestData.Gate().Evaluate(new UnmodeledForRestorePointTest
        {
            Description = "an action type the gate does not model", Reason = "test",
        });
        Assert.False(verdict.Allowed);
        Assert.Contains("unknown action type", verdict.Reason);
    }

    private sealed record UnmodeledForRestorePointTest : PlannedAction
    {
        public override string Kind => "test.unmodeled.rp";
        public override string TargetSignature() => "test.unmodeled.rp|x";
    }

    // ============================================================ 4) Executor Dispatch routing ===

    [Fact]
    public void GatedExecutor_dispatches_a_restore_point_to_the_creator_seam()
    {
        var creator = new FakeRestorePointCreator();
        using var fx = new RestorePointExecutorFixture(TestData.Gate(), creator);

        var rp = RestorePoint();
        var plan = new OperationPlan("rp", "uninstall", new PlannedAction[] { rp }, T0);
        ExecutionReport report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.True(report.Authorized);
        Assert.Equal(1, creator.CreateCount);
        Assert.Same(rp, creator.Last);
        Assert.All(report.Results, r => Assert.Equal(ActionStatus.Done, r.Status));
    }

    [Fact]
    public void GatedExecutor_runs_the_restore_point_BEFORE_its_destructive_neighbor()
    {
        // Co-staged order: restore point first, then the destructive action — proven via dispatch order.
        var creator = new FakeRestorePointCreator();
        var adapters = new RecordingAdapters();
        string logPath = Path.Combine(Path.GetTempPath(), "wck-rp-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var executor = new GatedExecutor(
            TestData.Gate(), new ExecutionLog(logPath, new LogRedactor(null, null)),
            adapters.File, adapters.Registry, adapters.Service, adapters.Task, adapters.Process, adapters.Copy,
            creator);
        try
        {
            var rp = RestorePoint();
            var cmd = OfficialUninstaller();
            var plan = new OperationPlan("rp+official", "uninstall", new PlannedAction[] { rp, cmd }, T0);
            ExecutionReport report = executor.ExecuteWithReport(plan, plan.ComputeHash());

            Assert.True(report.Authorized);
            Assert.Equal(1, creator.CreateCount);                  // restore point created
            var ranCommand = Assert.Single(adapters.Dispatched.OfType<CommandAction>());
            Assert.Same(cmd, ranCommand);                          // the uninstaller ran
            // The restore point's done-result precedes the command's in plan order.
            Assert.Equal(rp.Id, report.Results[0].ActionId);
            Assert.Equal(cmd.Id, report.Results[1].ActionId);
        }
        finally { try { File.Delete(logPath); } catch { /* best-effort */ } }
    }

    [Fact]
    public void GatedExecutor_fails_closed_when_no_creator_is_wired()
    {
        // No IRestorePointCreator → the fail-closed default THROWS on dispatch → recorded Failed + plan stops.
        using var fx = new ExecutorFixture(TestData.Gate()); // its GatedExecutor has no restore-point creator
        var rp = RestorePoint();
        var plan = new OperationPlan("rp", "uninstall", new PlannedAction[] { rp }, T0);
        ExecutionReport report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.True(report.Authorized);                            // the gate allowed it (pure system call)
        var result = Assert.Single(report.Results);
        Assert.Equal(ActionStatus.Failed, result.Status);          // but dispatch failed closed (no creator)
    }

    // ============================================================ 5) Capability probe logic ======

    [Theory]
    [InlineData(true, true, true)]    // SR on + elevated → available
    [InlineData(true, false, false)]  // SR on but not elevated → unavailable
    [InlineData(false, true, false)]  // elevated but SR off → unavailable
    [InlineData(false, false, false)] // neither → unavailable
    public void CapabilityProbe_is_available_only_when_SR_enabled_AND_elevated(bool srEnabled, bool elevated, bool expected)
    {
        var probe = new DefaultRestorePointCapabilityProbe(
            new FakeSrConfig(srEnabled), new FakeElevation(elevated));
        Assert.Equal(expected, probe.IsAvailable());
    }

    // =================================== 6) Creator re-checks capability before the Win32 call ====
    // PR-5 audit FIX 2: SRSetRestorePointW can report SUCCESS while System Restore is OFF (a fake guarantee).
    // The creator must re-check the probe FIRST and throw an honest failure when SR is unavailable, so a
    // non-UI caller (or a TOCTOU flip after the UI probed) can never get a fake restore point. Host-safe: the
    // throw happens BEFORE any P/Invoke, so this runs on a normal machine without creating a real restore point.

    [Fact]
    public void Win32RestorePointCreator_throws_before_any_Win32_call_when_capability_is_unavailable()
    {
        var creator = new Win32RestorePointCreator(new FakeCapability(available: false));

        // The throw is the capability re-check, NOT a Win32 failure: it never reaches SRSetRestorePointW.
        var ex = Assert.Throws<InvalidOperationException>(() => creator.Create(RestorePoint()));
        Assert.Contains("System Restore is not available", ex.Message);
    }

    [Fact]
    public void GatedExecutor_fails_closed_when_SR_is_unavailable_at_execution_time()
    {
        // End-to-end honesty: the real creator over an UNAVAILABLE probe → the restore-point action fails →
        // the executor records Failed (and, in a co-staged plan, the destructive plan would then STOP). The
        // user opted into a safety net that isn't there, so the deletion must not proceed.
        var creator = new Win32RestorePointCreator(new FakeCapability(available: false));
        using var fx = new RestorePointExecutorFixture(TestData.Gate(), creator);

        var plan = new OperationPlan("rp", "uninstall", new PlannedAction[] { RestorePoint() }, T0);
        ExecutionReport report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.True(report.Authorized);                            // the gate allowed it (pure system call)
        var result = Assert.Single(report.Results);
        Assert.Equal(ActionStatus.Failed, result.Status);          // but the creator failed closed (SR off)
    }

    // ============================================================ fakes ===========================

    private sealed class FakeCapability(bool available) : IRestorePointCapabilityProbe
    {
        public bool IsAvailable() => available;
    }

    private sealed class FakeRestorePointCreator : IRestorePointCreator
    {
        public int CreateCount { get; private set; }
        public CreateRestorePointAction? Last { get; private set; }
        public void Create(CreateRestorePointAction action)
        {
            CreateCount++;
            Last = action;
        }
    }

    private sealed class FakeSrConfig(bool enabled) : ISystemRestoreConfigProbe
    {
        public bool IsSystemRestoreEnabled() => enabled;
    }

    private sealed class FakeElevation(bool elevated) : IElevationProbe
    {
        public bool IsElevated() => elevated;
    }

    /// <summary>A GatedExecutor over recording fakes PLUS a supplied restore-point creator.</summary>
    private sealed class RestorePointExecutorFixture : IDisposable
    {
        private readonly string _logPath;
        public GatedExecutor Executor { get; }

        public RestorePointExecutorFixture(SafetyGate gate, IRestorePointCreator creator)
        {
            var adapters = new RecordingAdapters();
            _logPath = Path.Combine(Path.GetTempPath(), "wck-rpfx-" + Guid.NewGuid().ToString("N") + ".jsonl");
            Executor = new GatedExecutor(
                gate, new ExecutionLog(_logPath, new LogRedactor(null, null)),
                adapters.File, adapters.Registry, adapters.Service, adapters.Task, adapters.Process, adapters.Copy,
                creator);
        }

        public void Dispose() { try { File.Delete(_logPath); } catch { /* best-effort */ } }
    }
}

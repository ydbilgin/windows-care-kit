using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Execution.Adapters;
using WindowsCareKit.Tests.TestInfra;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests.Step4;

/// <summary>
/// PR-5 destructive proof (sandbox-only, UI decision §G / Hard rules): create a GENUINE Windows System Restore
/// point through the PRODUCTION gate (<see cref="ProtectedResources.ForCurrentSystem"/>) + the real
/// <see cref="GatedExecutor"/> + the real <see cref="Win32RestorePointCreator"/> (SRSetRestorePointW). This is
/// system-modifying and elevation-gated, so it must NOT run on a normal host: it is a
/// <see cref="DisposableFactAttribute"/> (statically SKIPPED off a disposable machine) and its first statement
/// is the fail-closed guard. On this host it reports SKIPPED — which is correct.
/// </summary>
public class RestorePointDisposableTests
{
    private static readonly DateTime T0 = new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

    [DisposableFact]
    [Trait("Category", TestCategories.Destructive)]
    public void Real_restore_point_is_created_through_the_production_executor()
    {
        DisposableMachineGuard.RequireDisposableOrSkip();

        string logPath = Path.Combine(Path.GetTempPath(), "wck-rp-disp-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var gate = new SafetyGate(ProtectedResources.ForCurrentSystem(), new Win32PathCanonicalizer());
            // The production creator re-checks the real capability probe before SRSetRestorePointW (PR-5 FIX 2).
            // Capability is environment-dependent: a System-Protection-ON lab VM has it (a real point is created),
            // but Windows Sandbox does NOT (no VSS / System Protection) — there the creator honestly refuses. The
            // assertions below branch on the machine's ACTUAL capability, so neither environment is a false pass.
            var capability = new DefaultRestorePointCapabilityProbe(
                new Win32SystemRestoreConfigProbe(), new Win32ElevationProbe());
            var executor = new GatedExecutor(
                gate, new ExecutionLog(logPath, new LogRedactor(null, null)),
                new RecycleBinFileDeleteAdapter(), new RegistryDeleteAdapter(Path.GetTempPath()),
                new ServiceControlAdapter(), new ScheduledTaskAdapter(), new ProcessAdapter(), new CopyAdapter(),
                new Win32RestorePointCreator(capability));

            var rp = new CreateRestorePointAction
            {
                RestorePointName = "Windows Care Kit — disposable test " + Guid.NewGuid().ToString("N")[..8],
                Description = "disposable restore-point creation test",
                Reason = "PR-5 destructive proof",
                Risk = RiskLevel.Info,
                Undo = UndoCapability.None,
                // IsProtective is type-bound true (PR-5 FIX 1) — automatic, no longer set here.
            };
            var plan = new OperationPlan("rp", "uninstall", new PlannedAction[] { rp }, T0);

            // The SAME signal the creator re-checks at execution, sampled once so the assertion matches exactly
            // what drove the production outcome (no double-probe ambiguity).
            bool srAvailable = capability.IsAvailable();

            ExecutionReport report = executor.ExecuteWithReport(plan, plan.ComputeHash());

            Assert.True(report.Authorized);
            var result = Assert.Single(report.Results);

            if (srAvailable)
            {
                // SR genuinely enabled + elevated → a REAL restore point was created via SRSetRestorePointW.
                Assert.Equal(ActionStatus.Done, result.Status);
            }
            else
            {
                // SR genuinely unavailable (e.g. Windows Sandbox: no System Protection) → the creator MUST refuse
                // to fake it; the plan records Failed with the honest reason. This proves PR-5 FIX 2 fails closed.
                // Pin the CAUSE, don't just narrate it: a [DisposableFact] always runs elevated (step4-run.cmd
                // fails loudly otherwise), so the only false signal that can land us here is SR-config being off —
                // exactly the Windows Sandbox reality. Assert both so the diagnosis is proven, not assumed.
                Assert.True(new Win32ElevationProbe().IsElevated());
                Assert.False(new Win32SystemRestoreConfigProbe().IsSystemRestoreEnabled());
                Assert.Equal(ActionStatus.Failed, result.Status);
                Assert.Contains("System Restore is not available", result.Detail, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { try { File.Delete(logPath); } catch { /* best-effort */ } }
    }
}

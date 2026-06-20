using Microsoft.Win32;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Execution;
using WindowsCareKit.Tests.TestInfra;
using Xunit;
using CoreHive = WindowsCareKit.Core.Planning.RegistryHive;

namespace WindowsCareKit.Tests.Step4;

/// <summary>
/// Step 4 Tier B — genuinely destructive, machine-wide + admin sinks driven through the PRODUCTION gate and
/// the REAL adapters via <see cref="RealExecutorFixture"/>. Every target is SELF-PROVISIONED throwaway state
/// (a <c>\WindowsCareKit.Tests\WCK_&lt;guid&gt;</c> task, a STOPPED <c>WCK_Test_&lt;guid&gt;</c> service, an
/// <c>HKLM\SOFTWARE\WindowsCareKit.Tests\&lt;guid&gt;</c> key) — never an OS object. These must NOT run on a
/// normal host: each is <c>[DisposableFact]</c> (statically SKIPPED off a disposable machine) + carries the
/// Destructive trait, and its FIRST statement is <see cref="DisposableMachineGuard.RequireDisposableOrSkip"/>
/// (fail-closed). On this host they report SKIPPED, which is correct. The sandbox harness guarantees elevation;
/// the core assertions therefore do NOT silently early-return on missing elevation — only genuine environmental
/// unavailability (e.g. the Task Scheduler service being down) guard-skips, and that is made VISIBLE.
/// </summary>
public class DisposableSinkTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc);

    // ----------------------------------------------------------------------------------------------------
    // B1. Scheduled task disable + delete, through the real gate + COM Task Scheduler adapter.
    //     Self-provisioned under \WindowsCareKit.Tests\WCK_<guid> (harmless whoami.exe); NEVER \Microsoft\Windows\**.
    // ----------------------------------------------------------------------------------------------------

    [DisposableFact]
    [Trait("Category", TestCategories.Destructive)]
    public void B1_ScheduledTask_disable_then_delete_through_the_real_gate_and_adapter()
    {
        DisposableMachineGuard.RequireDisposableOrSkip();

        using var fx = new RealExecutorFixture();
        using var toDisable = new DisposableTaskFixture();
        using var toDelete = new DisposableTaskFixture();

        // Environmental unavailability (Task Scheduler service down) is made VISIBLE, not a vacuous pass.
        Assert.True(toDisable.Available, "task provisioning unavailable: " + toDisable.ProvisionDetail);
        Assert.True(toDelete.Available, "task provisioning unavailable: " + toDelete.ProvisionDetail);

        var disable = new TaskDeleteAction
        {
            TaskPath = toDisable.TaskPath, Operation = TaskOperation.Disable,
            Description = "disable test task", Reason = "Tier B",
        };
        var delete = new TaskDeleteAction
        {
            TaskPath = toDelete.TaskPath, Operation = TaskOperation.Delete,
            Description = "delete test task", Reason = "Tier B",
        };

        var plan = new OperationPlan("t", "step4", new PlannedAction[] { disable, delete }, T0);
        ExecutionReport report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.True(report.Authorized, string.Join(",", report.Results.Select(r => r.Detail)));
        Assert.True(report.Results.All(r => r.Status == ActionStatus.Done),
            string.Join(",", report.Results.Select(r => $"{r.Kind}:{r.Status}:{r.Detail}")));

        // Disabled task still EXISTS but is now disabled; deleted task is GONE.
        Assert.True(toDisable.TaskExists());
        Assert.True(toDisable.IsDisabled());
        Assert.False(toDelete.TaskExists());
    }

    // ----------------------------------------------------------------------------------------------------
    // B2. Service disable + delete, through the real gate + SCM (advapi32) adapter.
    //     Self-provisioned STOPPED WCK_Test_<guid>; NO running-service Stop is performed.
    // ----------------------------------------------------------------------------------------------------

    [DisposableFact]
    [Trait("Category", TestCategories.Destructive)]
    public void B2_Service_disable_then_delete_through_the_real_gate_and_adapter()
    {
        DisposableMachineGuard.RequireDisposableOrSkip();

        using var fx = new RealExecutorFixture();
        using var toDisable = new DisposableServiceFixture();
        using var toDelete = new DisposableServiceFixture();

        var disable = new ServiceDeleteAction
        {
            ServiceName = toDisable.ServiceName, Operation = ServiceOperation.Disable,
            Description = "disable test service", Reason = "Tier B",
        };
        var delete = new ServiceDeleteAction
        {
            ServiceName = toDelete.ServiceName, Operation = ServiceOperation.Delete,
            Description = "delete test service", Reason = "Tier B",
        };

        var plan = new OperationPlan("t", "step4", new PlannedAction[] { disable, delete }, T0);
        ExecutionReport report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.True(report.Authorized, string.Join(",", report.Results.Select(r => r.Detail)));
        Assert.True(report.Results.All(r => r.Status == ActionStatus.Done),
            string.Join(",", report.Results.Select(r => $"{r.Kind}:{r.Status}:{r.Detail}")));

        // Disabled service still EXISTS with start type DISABLED; deleted service is GONE.
        Assert.True(toDisable.ServiceExists());
        Assert.True(toDisable.IsDisabled());
        Assert.False(toDelete.ServiceExists());
    }

    // ----------------------------------------------------------------------------------------------------
    // B3. HKLM registry value + key delete through the real gate + adapter, with a real .reg backup.
    //     Self-provisioned HKLM\SOFTWARE\WindowsCareKit.Tests\<guid> — proves the LocalMachine view path.
    // ----------------------------------------------------------------------------------------------------

    [DisposableFact]
    [Trait("Category", TestCategories.Destructive)]
    public void B3_HKLM_registry_value_then_key_delete_through_the_real_gate_writes_a_reg_backup()
    {
        DisposableMachineGuard.RequireDisposableOrSkip();

        using var fx = new RealExecutorFixture();
        using var key = new DisposableRegistryKeyFixture(CoreHive.LocalMachine);

        Assert.True(key.KeyExists());

        var valueDelete = new RegistryDeleteAction
        {
            Hive = CoreHive.LocalMachine, SubKeyPath = key.SubKeyPath, ValueName = "Marker",
            Description = "delete HKLM value", Reason = "Tier B",
        };
        var keyDelete = new RegistryDeleteAction
        {
            Hive = CoreHive.LocalMachine, SubKeyPath = key.SubKeyPath,
            Description = "delete HKLM key", Reason = "Tier B",
        };

        var plan = new OperationPlan("t", "step4", new PlannedAction[] { valueDelete, keyDelete }, T0);
        ExecutionReport report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.True(report.Authorized, string.Join(",", report.Results.Select(r => r.Detail)));
        Assert.True(report.Results.All(r => r.Status == ActionStatus.Done),
            string.Join(",", report.Results.Select(r => $"{r.Kind}:{r.Status}:{r.Detail}")));

        // The whole HKLM key (value + child) is gone — proving the LocalMachine 64-bit view path.
        Assert.False(key.KeyExists());

        // The real adapter exported at least one standard .reg backup before deleting.
        string[] regFiles = Directory.GetFiles(fx.RegBackupDir, "*.reg");
        Assert.NotEmpty(regFiles);
        Assert.StartsWith("Windows Registry Editor Version 5.00", File.ReadAllText(regFiles[0]));
    }
}

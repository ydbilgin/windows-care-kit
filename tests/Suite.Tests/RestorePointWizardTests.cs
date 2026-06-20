using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// PR-5 wizard co-staging (host-safe). The restore point is flipped on from the capability probe and
/// PREPENDED to the official-uninstaller plan (so it is always a neighbor of a destructive action, never its
/// own plan). The Irreversible tier still arises from the uninstaller, per UI decision §5. Nothing real runs:
/// the executor + capability probe are fakes.
/// </summary>
public class RestorePointWizardTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static InstalledApp MachineWideApp() => TestData.App(
        displayName: "SomeApp", publisher: "SomeVendor", source: InstalledAppSource.MachineWide64,
        uninstall: "\"C:\\Program Files\\SomeApp\\uninst.exe\" /S");

    private static UninstallWizardViewModel BuildWizard(FakeExecutor executor, bool restorePointAvailable)
    {
        var i18n = new I18n();
        i18n.Load("tr");
        return new UninstallWizardViewModel(i18n, TestData.Gate(), new FakeLeftoverProbe(), executor,
            utcNow: () => T0, restorePointCapability: new FakeCapability(restorePointAvailable));
    }

    [Fact]
    public void Toggle_is_available_and_default_on_when_SR_is_available()
    {
        var wizard = BuildWizard(new FakeExecutor(), restorePointAvailable: true);
        wizard.Open(MachineWideApp());

        Assert.True(wizard.RestorePointAvailable);   // probed available → toggle enabled
        Assert.True(wizard.RestorePointEnabled);     // safe default ON when available
    }

    [Fact]
    public void Toggle_stays_disabled_with_the_honest_reason_when_SR_unavailable()
    {
        var wizard = BuildWizard(new FakeExecutor(), restorePointAvailable: false);
        wizard.Open(MachineWideApp());

        Assert.False(wizard.RestorePointAvailable);  // not available → toggle disabled
        Assert.False(wizard.RestorePointEnabled);    // off
        // The honest disabled reason (needs admin / SR off), never a fake guarantee.
        Assert.Equal(wizard.I18n["uninstall.wizard.prep.restorePoint.reason"], wizard.RestorePointReason);
    }

    [Fact]
    public void Toggle_absent_capability_probe_is_unavailable()
    {
        // No capability probe injected at all (the PR-4 shape) → hard-unavailable, never crashes.
        var i18n = new I18n();
        i18n.Load("tr");
        var wizard = new UninstallWizardViewModel(i18n, TestData.Gate(), new FakeLeftoverProbe(),
            new FakeExecutor(), () => T0);
        wizard.Open(MachineWideApp());

        Assert.False(wizard.RestorePointAvailable);
        Assert.False(wizard.RestorePointEnabled);
    }

    [Fact]
    public void StageOfficial_PREPENDS_the_restore_point_as_a_neighbor_when_enabled()
    {
        var executor = new FakeExecutor();
        var wizard = BuildWizard(executor, restorePointAvailable: true);
        wizard.Open(MachineWideApp());
        Assert.True(wizard.RestorePointEnabled);

        wizard.RunOfficialCommand.Execute(null);   // stage official → ConfirmGate #1 (no execution yet)
        wizard.Gate.ApproveCommand.Execute(null);

        // Pump the async approve to completion.
        SpinUntil(() => executor.CallCount == 1);

        OperationPlan ran = Assert.IsType<OperationPlan>(executor.LastPlan);
        Assert.Equal(2, ran.Actions.Count);
        // The restore point is FIRST (prepended) and IS PROTECTIVE; the uninstaller follows.
        var rp = Assert.IsType<CreateRestorePointAction>(ran.Actions[0]);
        Assert.True(rp.IsProtective);
        var cmd = Assert.IsType<CommandAction>(ran.Actions[1]);
        Assert.Contains("uninst.exe", cmd.FileName);
        // Never a standalone plan: the restore point coexists with the destructive uninstaller.
        Assert.Contains(ran.Actions, a => a is CommandAction);
    }

    [Fact]
    public void StageOfficial_does_NOT_co_stage_when_toggle_is_off()
    {
        var executor = new FakeExecutor();
        var wizard = BuildWizard(executor, restorePointAvailable: true);
        wizard.Open(MachineWideApp());
        wizard.RestorePointEnabled = false; // user opts out of the extra layer

        wizard.RunOfficialCommand.Execute(null);
        wizard.Gate.ApproveCommand.Execute(null);
        SpinUntil(() => executor.CallCount == 1);

        OperationPlan ran = Assert.IsType<OperationPlan>(executor.LastPlan);
        // Only the official uninstaller — no restore point prepended.
        Assert.Single(ran.Actions);
        Assert.IsType<CommandAction>(ran.Actions[0]);
        Assert.DoesNotContain(ran.Actions, a => a is CreateRestorePointAction);
    }

    [Fact]
    public void StageOfficial_does_NOT_co_stage_when_unavailable_even_if_enabled_flag_set()
    {
        var executor = new FakeExecutor();
        var wizard = BuildWizard(executor, restorePointAvailable: false);
        wizard.Open(MachineWideApp());
        // Force the enabled flag true despite unavailability — the co-stage guard requires BOTH.
        wizard.RestorePointEnabled = true;

        wizard.RunOfficialCommand.Execute(null);
        wizard.Gate.ApproveCommand.Execute(null);
        SpinUntil(() => executor.CallCount == 1);

        OperationPlan ran = Assert.IsType<OperationPlan>(executor.LastPlan);
        Assert.Single(ran.Actions);
        Assert.DoesNotContain(ran.Actions, a => a is CreateRestorePointAction);
    }

    [Fact]
    public void Co_staged_plan_tier_is_Irreversible_driven_by_the_uninstaller_not_the_restore_point()
    {
        var executor = new FakeExecutor();
        var wizard = BuildWizard(executor, restorePointAvailable: true);
        wizard.Open(MachineWideApp());

        wizard.RunOfficialCommand.Execute(null);

        // The gate is open at the Irreversible tier (the uninstaller's Undo=None drives it); the restore point
        // is exempt. Two rows are shown (restore point + uninstaller) — never a standalone restore-point plan.
        Assert.True(wizard.Gate.IsOpen);
        Assert.Equal(ConfirmTier.Irreversible, wizard.Gate.Tier);
        Assert.Equal(2, wizard.Gate.Rows.Count);
    }

    // Generous ceiling (flaky-fix 2026-06-21): the happy path exits the instant condition() is true (~ms),
    // so a large cap never slows passing tests; it only prevents false timeouts when the async settle runs
    // slow under CI/Release load (was 2000ms → flaked on CI while green on host).
    private static void SpinUntil(Func<bool> condition, int timeoutMs = 30_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            Thread.Sleep(10);
        Assert.True(condition(), "operation did not complete in time");
    }

    private sealed class FakeCapability(bool available) : IRestorePointCapabilityProbe
    {
        public bool IsAvailable() => available;
    }

    private sealed class FakeExecutor : IExecutor
    {
        public int CallCount { get; private set; }
        public OperationPlan? LastPlan { get; private set; }
        public string? LastHash { get; private set; }

        public ExecutionOutcome Execute(OperationPlan plan, string approvedPlanHash)
        {
            CallCount++;
            LastPlan = plan;
            LastHash = approvedPlanHash;
            return new ExecutionOutcome(true, "faked");
        }
    }
}

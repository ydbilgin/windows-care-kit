using WindowsCareKit.Core.Planning;
using WindowsCareKit.Execution;
using WindowsCareKit.Tests.Execution;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>The executor authorizes FIRST and runs nothing when authorization fails (spec §3 fail-closed).</summary>
public class GatedExecutorAuthorizationTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static OperationPlan CleanPlan() =>
        new("t", "uninstall", new[] { TestData.FileDelete(@"C:\Program Files\SomeApp") }, T0);

    [Fact]
    public void Refuses_on_a_bad_hash_and_never_calls_an_adapter()
    {
        using var fx = new ExecutorFixture();
        fx.Adapters.ThrowOnAnyCall = true; // any adapter call would fail the test

        var plan = CleanPlan();
        var report = fx.Executor.ExecuteWithReport(plan, "deadbeef");

        Assert.False(report.Authorized);
        Assert.All(report.Results, r => Assert.Equal(ActionStatus.NotRun, r.Status));
        Assert.Empty(fx.Adapters.Calls);
        Assert.Contains(fx.LogLines(), l => l.Contains("plan.refused"));
    }

    [Fact]
    public void Refuses_when_no_approved_hash_is_supplied()
    {
        using var fx = new ExecutorFixture();
        fx.Adapters.ThrowOnAnyCall = true;

        var plan = CleanPlan();
        var report = fx.Executor.ExecuteWithReport(plan, "   ");

        Assert.False(report.Authorized);
        Assert.Empty(fx.Adapters.Calls);
    }

    [Fact]
    public void Refuses_a_plan_with_a_gate_blocked_action_even_with_the_matching_hash()
    {
        using var fx = new ExecutorFixture();
        fx.Adapters.ThrowOnAnyCall = true;

        // C:\Windows is protected → the gate blocks it → the WHOLE plan is refused at authorization.
        var plan = new OperationPlan("t", "uninstall", new[] { TestData.FileDelete(@"C:\Windows") }, T0);
        var report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.False(report.Authorized);
        Assert.Empty(fx.Adapters.Calls);
        Assert.Equal(0, report.DoneCount);
    }

    [Fact]
    public void Execute_returns_a_refused_outcome_when_authorization_fails()
    {
        using var fx = new ExecutorFixture();
        var plan = CleanPlan();

        var outcome = fx.Executor.Execute(plan, "wrong-hash");

        Assert.False(outcome.Ran);
    }

    [Fact]
    public void Authorizes_and_runs_a_clean_plan_with_the_matching_hash()
    {
        using var fx = new ExecutorFixture();
        var plan = CleanPlan();

        var report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.True(report.Authorized);
        Assert.True(report.AllDone);
        Assert.Single(fx.Adapters.Calls);
    }
}

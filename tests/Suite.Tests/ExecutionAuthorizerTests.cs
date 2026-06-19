using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Planning;
using Xunit;

namespace WindowsCareKit.Tests;

public class ExecutionAuthorizerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static OperationPlan CleanPlan() =>
        new("t", "uninstall", new[] { TestData.FileDelete(@"C:\Program Files\SomeApp") }, T0);

    [Fact]
    public void Authorizes_a_clean_plan_with_the_matching_hash()
    {
        var plan = CleanPlan();
        var auth = ExecutionAuthorizer.Authorize(plan, plan.ComputeHash(), TestData.Gate());
        Assert.True(auth.Authorized, auth.Reason);
    }

    [Fact]
    public void Refuses_when_the_hash_does_not_match_the_approved_one()
    {
        var plan = CleanPlan();
        var auth = ExecutionAuthorizer.Authorize(plan, "deadbeef", TestData.Gate());
        Assert.False(auth.Authorized);
        Assert.Contains("TOCTOU", auth.Reason);
    }

    [Fact]
    public void Refuses_when_no_approved_hash_is_supplied()
    {
        var plan = CleanPlan();
        Assert.False(ExecutionAuthorizer.Authorize(plan, "", TestData.Gate()).Authorized);
        Assert.False(ExecutionAuthorizer.Authorize(plan, "   ", TestData.Gate()).Authorized);
    }

    [Fact]
    public void Refuses_a_plan_that_contains_a_blocked_action_even_with_matching_hash()
    {
        var plan = new OperationPlan("t", "uninstall", new[]
        {
            TestData.FileDelete(@"C:\Windows"),   // blocked
        }, T0);

        var auth = ExecutionAuthorizer.Authorize(plan, plan.ComputeHash(), TestData.Gate());

        Assert.False(auth.Authorized);
        Assert.Contains("blocked", auth.Reason);
    }
}

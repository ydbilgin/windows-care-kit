using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

public class OperationPlanTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Hash_is_stable_for_the_same_actions_regardless_of_timestamp()
    {
        var a = new OperationPlan("t", "uninstall", new[] { TestData.FileDelete(@"C:\X\a") }, T0);
        var b = new OperationPlan("t", "uninstall", new[] { TestData.FileDelete(@"C:\X\a") }, T0.AddHours(5));
        Assert.Equal(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void Hash_changes_when_a_target_changes()
    {
        var a = new OperationPlan("t", "m", new[] { TestData.FileDelete(@"C:\X\a") }, T0);
        var b = new OperationPlan("t", "m", new[] { TestData.FileDelete(@"C:\X\b") }, T0);
        Assert.NotEqual(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void Hash_changes_when_risk_changes()
    {
        var low = TestData.FileDelete(@"C:\X\a") with { Risk = RiskLevel.Low };
        var high = TestData.FileDelete(@"C:\X\a") with { Risk = RiskLevel.High };
        var a = new OperationPlan("t", "m", new[] { low }, T0);
        var b = new OperationPlan("t", "m", new[] { high }, T0);
        Assert.NotEqual(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void Hash_changes_when_command_elevation_changes()
    {
        var notElevated = TestData.Command(@"C:\a\u.exe", "/x") with { RequiresElevation = false };
        var elevated = TestData.Command(@"C:\a\u.exe", "/x") with { RequiresElevation = true };
        var a = new OperationPlan("t", "m", new[] { notElevated }, T0);
        var b = new OperationPlan("t", "m", new[] { elevated }, T0);
        Assert.NotEqual(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void Hash_changes_when_service_operation_changes_Stop_vs_Delete()
    {
        // Item 1(a): the ServiceDeleteAction.Operation is part of TargetSignature(), so a Stop and a Delete of
        // the SAME service hash differently. Without Operation in the signature a TOCTOU swap (approve Stop,
        // execute Delete) would re-validate against the same hash and slip through.
        var stop = TestData.Service("SomeVendorSvc", ServiceOperation.Stop);
        var delete = TestData.Service("SomeVendorSvc", ServiceOperation.Delete);
        var a = new OperationPlan("t", "m", new[] { stop }, T0);
        var b = new OperationPlan("t", "m", new[] { delete }, T0);
        Assert.NotEqual(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void Hash_changes_when_command_argument_values_change_same_filename_and_argc()
    {
        // Item 1(b): two commands with the SAME FileName and the SAME argument COUNT but different argument
        // VALUES must hash differently — this binds CommandAction.ArgHash(). If only argc were hashed, an
        // attacker could swap "/x {GUID-A}" for "/x {GUID-B}" (same shape) after approval undetected.
        var a = new OperationPlan("t", "m", new[] { TestData.Command(@"C:\a\u.exe", "/x", "{AAAA}") }, T0);
        var b = new OperationPlan("t", "m", new[] { TestData.Command(@"C:\a\u.exe", "/x", "{BBBB}") }, T0);

        // Guard: same FileName and same argument count (so the difference is purely the VALUES → ArgHash).
        var ca = (CommandAction)a.Actions[0];
        var cb = (CommandAction)b.Actions[0];
        Assert.Equal(ca.FileName, cb.FileName);
        Assert.Equal(ca.Arguments.Count, cb.Arguments.Count);

        Assert.NotEqual(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void MaxRisk_and_IsEmpty()
    {
        var empty = new OperationPlan("t", "m", Array.Empty<PlannedAction>(), T0);
        Assert.True(empty.IsEmpty);
        Assert.Equal(RiskLevel.Info, empty.MaxRisk);

        var plan = new OperationPlan("t", "m", new[]
        {
            TestData.FileDelete(@"C:\X\a") with { Risk = RiskLevel.Low },
            TestData.FileDelete(@"C:\X\b") with { Risk = RiskLevel.High },
        }, T0);
        Assert.False(plan.IsEmpty);
        Assert.Equal(RiskLevel.High, plan.MaxRisk);
    }

    [Fact]
    public void Constructor_rejects_nulls()
    {
        Assert.Throws<ArgumentNullException>(() => new OperationPlan(null!, "m", Array.Empty<PlannedAction>(), T0));
        Assert.Throws<ArgumentNullException>(() => new OperationPlan("t", null!, Array.Empty<PlannedAction>(), T0));
        Assert.Throws<ArgumentNullException>(() => new OperationPlan("t", "m", null!, T0));
    }

    [Fact]
    public void Validate_reports_blocked_actions()
    {
        var plan = new OperationPlan("t", "uninstall", new PlannedAction[]
        {
            TestData.FileDelete(@"C:\Program Files\SomeApp"),  // allowed
            TestData.FileDelete(@"C:\Windows"),                // blocked
        }, T0);

        var result = TestData.Gate().Validate(plan);

        Assert.False(result.AllAllowed);
        Assert.Single(result.Blocked);
        Assert.Equal(@"C:\Windows", ((FileDeleteAction)result.Blocked[0].Action).Path);
    }
}

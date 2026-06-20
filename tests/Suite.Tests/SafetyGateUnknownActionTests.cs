using WindowsCareKit.Core.Planning;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Guards the gate's fail-closed default. PR-3 adds new classification/plan-building code around the gate;
/// this test proves nothing introduced an accidental catch-all — an action type the gate does not model
/// still falls to the <c>_ =&gt; Block("unknown action type: …")</c> arm and is refused (spec §3, §5/§6).
/// </summary>
public class SafetyGateUnknownActionTests
{
    /// <summary>A typed action the gate has no case for — exists only to exercise the unknown-type arm.</summary>
    private sealed record UnmodeledAction : PlannedAction
    {
        public override string Kind => "test.unmodeled";
        public override string TargetSignature() => "test.unmodeled|x";
    }

    [Fact]
    public void Unknown_action_type_hits_the_block_default_arm()
    {
        var verdict = TestData.Gate().Evaluate(new UnmodeledAction
        {
            Description = "an action type the gate does not model",
            Reason = "test",
        });

        Assert.False(verdict.Allowed);
        Assert.Contains("unknown action type", verdict.Reason);
    }

    [Fact]
    public void Unknown_action_type_blocks_the_whole_plan()
    {
        var plan = new OperationPlan("unmodeled", "test",
            new[] { new UnmodeledAction { Description = "x", Reason = "test" } }, DateTime.UtcNow);

        Assert.False(TestData.Gate().Validate(plan).AllAllowed);
    }
}

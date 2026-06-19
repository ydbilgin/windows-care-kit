using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Safety;

/// <summary>The gate's decision about a single action.</summary>
/// <param name="Allowed">False means the action is refused and must not run.</param>
/// <param name="Reason">Why it was allowed or blocked, surfaced in the UI and the log.</param>
public sealed record SafetyVerdict(bool Allowed, string Reason)
{
    public static SafetyVerdict Allow(string reason = "ok") => new(true, reason);
    public static SafetyVerdict Block(string reason) => new(false, reason);
}

/// <summary>The gate's decision about a whole plan.</summary>
public sealed record PlanValidationResult(
    bool AllAllowed,
    IReadOnlyList<ActionVerdict> Results)
{
    /// <summary>The subset of actions that were blocked (empty when the plan is fully approved).</summary>
    public IReadOnlyList<ActionVerdict> Blocked
        => Results.Where(r => !r.Verdict.Allowed).ToArray();
}

/// <summary>Pairs an action with the gate's verdict for it.</summary>
public sealed record ActionVerdict(PlannedAction Action, SafetyVerdict Verdict);

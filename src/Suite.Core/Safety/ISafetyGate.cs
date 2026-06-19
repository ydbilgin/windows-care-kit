using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Safety;

/// <summary>
/// The single gate every destructive action must pass through (spec §3). Evaluating an action is
/// pure policy over a <see cref="CanonicalPath"/>; the executor calls <see cref="Validate"/> both
/// when the plan is built and again right before execution (TOCTOU).
/// </summary>
public interface ISafetyGate
{
    /// <summary>Evaluate one action against the protected-resource policy.</summary>
    SafetyVerdict Evaluate(PlannedAction action);

    /// <summary>Evaluate every action in a plan; <see cref="PlanValidationResult.AllAllowed"/> gates execution.</summary>
    PlanValidationResult Validate(OperationPlan plan);
}

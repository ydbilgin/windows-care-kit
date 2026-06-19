using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Execution;

/// <summary>The result of authorizing a plan for execution.</summary>
/// <param name="Authorized">When false, nothing may run.</param>
/// <param name="Reason">Why authorization passed or failed.</param>
/// <param name="PlanHash">The plan hash computed at authorization time.</param>
public sealed record ExecutionAuthorization(bool Authorized, string Reason, string PlanHash);

/// <summary>
/// The chokepoint between an approved dry-run and any execution. A plan may only run if (1) the
/// SafetyGate re-validates every action as allowed *right now* and (2) the plan's hash still equals
/// the hash the user approved. (2) closes the TOCTOU window where the filesystem/registry changed
/// after the preview (spec §3). There is deliberately no path to execute an action that has not been
/// through here.
/// </summary>
public static class ExecutionAuthorizer
{
    public static ExecutionAuthorization Authorize(OperationPlan plan, string approvedPlanHash, ISafetyGate gate)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(gate);

        if (string.IsNullOrWhiteSpace(approvedPlanHash))
            return new ExecutionAuthorization(false, "no approved plan hash", plan.ComputeHash());

        PlanValidationResult validation = gate.Validate(plan);
        string currentHash = plan.ComputeHash();

        if (!validation.AllAllowed)
        {
            int blocked = validation.Blocked.Count;
            return new ExecutionAuthorization(false, $"safety gate blocked {blocked} action(s)", currentHash);
        }

        if (!string.Equals(currentHash, approvedPlanHash, StringComparison.Ordinal))
            return new ExecutionAuthorization(false, "plan changed since approval (TOCTOU)", currentHash);

        return new ExecutionAuthorization(true, "authorized", currentHash);
    }
}

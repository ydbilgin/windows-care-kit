using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>
/// Builds the leftover-deletion <see cref="OperationPlan"/> from the user-SELECTED candidates. This is the
/// PR-4 SELECTION-TIME invariant — NOT the live guard. The guard ACTIVE today is the scanner's
/// ProgramOwned-only filter in <see cref="LeftoverScanner"/>, which is what the staged plan actually flows
/// from; when PR-4 wires the 3-tier selection UI, the selected candidates are rebuilt here. Two invariants,
/// both enforced BEFORE the plan is hashed/authorized:
///
/// <list type="number">
/// <item>Only <see cref="LeftoverCandidate.Selected"/> candidates contribute actions.</item>
/// <item>Every contributing action MUST be <see cref="LeftoverClassification.ProgramOwned"/>. If a Shared or
/// Protected action ever reaches the builder it <b>throws</b> <see cref="LeftoverPlanBuildException"/> — it is
/// never silently dropped, because a Shared action in the build set means the selectability barrier was
/// bypassed and that must surface, not be swallowed.</item>
/// </list>
///
/// The returned plan flows through the EXISTING pipeline unchanged: it is hashed
/// (<see cref="OperationPlan.ComputeHash"/>), the <c>SafetyGate</c> re-evaluates it at stage, and the
/// <c>GatedExecutor</c> re-evaluates each action at run. This builder does NOT bypass any of that.
///
/// HONESTY (spec §6): the gate only re-blocks Protected, NOT Shared. A non-protected Shared vendor parent
/// key would pass the gate — what keeps it out of the staged plan is the scanner's ProgramOwned filter
/// (live) and, at selection time, this builder's ProgramOwned-only invariant. The gate does NOT cover Shared.
/// </summary>
public sealed class LeftoverPlanBuilder
{
    /// <summary>
    /// Build a deletion plan from <paramref name="candidates"/>, taking only the selected ProgramOwned ones.
    /// Selected candidates are de-duplicated by their canonical <see cref="PlannedAction.TargetSignature"/>, so
    /// two candidates that target the same resource yield a single action (a stable, predictable plan hash).
    /// </summary>
    /// <exception cref="ArgumentException">A candidate (or its action) is null.</exception>
    /// <exception cref="LeftoverPlanBuildException">
    /// A selected candidate is not ProgramOwned (defense in depth against a bypassed selectability check).
    /// </exception>
    public OperationPlan Build(InstalledApp app, IReadOnlyList<LeftoverCandidate> candidates, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(candidates);

        var actions = new List<PlannedAction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < candidates.Count; i++)
        {
            LeftoverCandidate? candidate = candidates[i];
            if (candidate is null)
                throw new ArgumentException($"candidate[{i}] is null", nameof(candidates));
            if (candidate.Action is null)
                throw new ArgumentException($"candidate[{i}].Action is null", nameof(candidates));

            if (!candidate.Selected)
                continue;

            // A selected non-ProgramOwned candidate must NEVER yield an action — throw before authorize.
            if (candidate.Classification != LeftoverClassification.ProgramOwned)
                throw new LeftoverPlanBuildException(candidate.Action, candidate.Classification);

            // Dedupe by canonical target signature so duplicate selections collapse to one action.
            if (seen.Add(candidate.Action.TargetSignature()))
                actions.Add(candidate.Action);
        }

        return new OperationPlan($"Clean up leftovers of {app.DisplayName}", "uninstall", actions, utc);
    }
}

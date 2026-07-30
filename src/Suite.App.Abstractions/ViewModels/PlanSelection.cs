using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.App.ViewModels;

/// <summary>
/// Builds the plan the user actually approved out of a dry-run plan plus the rows they vetoed.
/// <para>
/// The veto is enforced by the existing TOCTOU contract rather than by trust (spec §1.1): a subset is a NEW
/// <see cref="OperationPlan"/>, so its <see cref="OperationPlan.ComputeHash"/> — derived purely from the ordered
/// actions — differs from the source plan's. The subset is therefore what is previewed, what is hashed, what is
/// approved, and what is executed; a user who approves a three-action subset cannot have the five-action
/// superset run, because the gate re-validates the approved hash and would refuse. Computing the hash from one
/// plan while executing the other reintroduces exactly the defect this resolves.
/// </para>
/// </summary>
public static class PlanSelection
{
    /// <summary>
    /// Builds the plan the user actually approved: the source plan's actions filtered to the included rows,
    /// <b>in the source plan's original order</b> — never the view's sort order. Action order is an
    /// execution-safety property (restore point first, vendor uninstall second), so the view is not allowed to
    /// influence it; the rows only ever say WHICH actions survive, never in what sequence.
    /// <para>
    /// An action survives when a row names it (<see cref="PlanRow.Action"/>) and that row either is not vetoable
    /// — required steps are always retained — or is vetoable and included. Rows that name no action contribute
    /// nothing. The filter is therefore fail-closed: rows that do not describe this plan remove actions rather
    /// than smuggling extra ones in.
    /// </para>
    /// <para>
    /// A <see cref="PlanRow.IsSkipped"/> row is the one non-vetoable row that is NOT retained. "Not vetoable"
    /// covers two opposite meanings — a required step the engine always runs, and a blocked step it never runs —
    /// and retaining both would compose a plan that executes the very row the gate prints as <c>WON'T RUN</c>.
    /// The composed plan agrees with what the user was shown, which is the point of composing it at all.
    /// </para>
    /// </summary>
    public static OperationPlan Subset(OperationPlan source, IEnumerable<PlanRow> rows)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rows);

        // Reference identity, not record value equality: a row previews one specific action instance, and two
        // structurally identical actions in one plan must not have a veto on the first silently drop the second.
        var included = new HashSet<PlannedAction>(ReferenceEqualityComparer.Instance);
        foreach (PlanRow row in rows)
        {
            if (row?.Action is not { } action || row.IsSkipped)
                continue;

            if (!row.IsVetoable || row.IsIncluded)
                included.Add(action);
        }

        return new OperationPlan(
            source.Title,
            source.ModuleName,
            source.Actions.Where(included.Contains).ToArray(),
            source.CreatedAtUtc);
    }
}

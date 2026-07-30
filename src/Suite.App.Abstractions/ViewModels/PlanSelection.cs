using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.App.ViewModels;

/// <summary>
/// Builds the plan the user actually approved out of a dry-run plan plus the rows they were shown.
/// <para>
/// The veto is enforced by the existing TOCTOU contract rather than by trust (spec §1.1): a subset is a NEW
/// <see cref="OperationPlan"/>, so its <see cref="OperationPlan.ComputeHash"/> — derived purely from the ordered
/// actions — differs from the source plan's. The subset is therefore what is previewed, what is hashed, what is
/// approved, and what is executed; a user who approves a three-action subset cannot have the five-action
/// superset run, because the gate re-validates the approved hash and would refuse. Computing the hash from one
/// plan while executing the other reintroduces exactly the defect this resolves.
/// </para>
/// <para>
/// The hash binds WHICH actions run and IN WHAT ORDER. It does not yet bind an action's mutable payload through
/// dispatch (spec §2.0c): <c>CopyAction.Include</c> and its siblings are references to caller-owned lists, so a
/// list mutated after authorization changes the real effect. That is pre-existing in <c>Suite.Core</c> and is
/// tracked separately; it bounds what this type may claim, and this documentation is the honest statement of it.
/// </para>
/// </summary>
public static class PlanSelection
{
    /// <summary>
    /// Builds the plan the user actually approved: the source plan's actions filtered by the rows that describe
    /// them, <b>in the source plan's original order</b> — never the view's sort order. Action order is an
    /// execution-safety property (restore point first, vendor uninstall second), so the view is not allowed to
    /// influence it; the rows only ever say WHICH actions survive, never in what sequence.
    /// <para>
    /// <b>Rows bind to plan OCCURRENCES, not to action references.</b> A plan may contain the same action
    /// instance more than once, and one veto must control exactly one execution of it. Each row that makes a
    /// selection claim (<see cref="RowSelection.Required"/>, <see cref="RowSelection.OptionalIncluded"/>,
    /// <see cref="RowSelection.OptionalExcluded"/>) therefore claims one occurrence: rows are read in the order
    /// given, and the n-th row naming an action claims that action's n-th occurrence in source order. A row that
    /// finds no unclaimed occurrence is foreign or duplicate, and composition is refused.
    /// </para>
    /// <para>
    /// <b><see cref="RowSelection.Blocked"/> is a veto, not a claim, and it dominates.</b> A blocked row states
    /// its action will not run; it excludes EVERY occurrence of that action, and no other row — required,
    /// included, or a second alias — can resurrect it (spec §2.0 rule 1). It is also allowed to name an action
    /// the source does not contain: modules show their <c>WON'T RUN</c> rows next to the plan even when the
    /// planner already left those actions out, and a row that can only subtract can never smuggle anything in.
    /// </para>
    /// <para>
    /// <b>Source coverage is validated and composition REFUSES rather than silently dropping</b> (spec §2.0
    /// rule 2). Every action in <paramref name="source"/> must be claimed by exactly one row or vetoed by a
    /// blocked one. Fail-closed against ADDING an action was never enough: it was fail-OPEN against dropping a
    /// required one, so rows naming only the vendor uninstaller composed a plan without the restore point that
    /// protects it — and that malformed plan hashes validly, so no downstream gate can catch it.
    /// </para>
    /// <para>
    /// A row that names no action (<see cref="RowSelection.Unbound"/>, the legacy literal path) makes no claim
    /// at all: it neither survives, nor covers an action, nor blocks one.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="rows"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The rows do not describe this plan: an action is unrepresented, a row names an action the plan does not
    /// contain (or names it more times than it occurs), or a row is null. This is a COMPOSITION error — the
    /// caller built rows from a different plan than the one it is composing, or built none at all — so it is a
    /// programming error rather than user input, and there is no partial result worth returning. Callers must
    /// rebuild their rows from the plan they are about to compose; a stale row set must never be smoothed into a
    /// shorter plan.
    /// </exception>
    public static OperationPlan Subset(OperationPlan source, IEnumerable<PlanRow> rows)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rows);

        IReadOnlyList<PlannedAction> actions = source.Actions;

        // Reference identity, not record value equality: a row previews one specific action INSTANCE, and two
        // structurally identical actions in one plan must not have a veto on the first silently drop the second.
        var vetoed = new HashSet<PlannedAction>(ReferenceEqualityComparer.Instance);
        var claimants = new List<PlanRow>();

        foreach (PlanRow row in rows)
        {
            if (row is null)
                throw new ArgumentException("A null row cannot describe a plan occurrence.", nameof(rows));

            if (row.Action is not { } action)
                continue; // Unbound: rendered text only, so it makes no claim about this plan.

            if (row.Selection is RowSelection.Blocked)
            {
                vetoed.Add(action);
                continue;
            }

            // "Required" must not stay a caller convention: a caller that marks the restore point vetoable and
            // leaves it unchecked drops the protective step while its destructive neighbour still runs. The one
            // anchor that cannot be forged is Core's type-bound IsProtective, so a checkbox on such an action is
            // a composition error. (A protective action the ENGINE refuses arrives as a Blocked row above: the
            // user is told, so the composed plan and the row still agree.)
            if (row.IsVetoable && action.IsProtective)
            {
                throw new ArgumentException(
                    $"A protective action ({action.Kind}: {action.Description}) is offered as a vetoable row. "
                    + "It adds rollback state and is staged as the neighbour of the destructive action it "
                    + "protects, so a checkbox on it would let the protection disappear while the destruction "
                    + "remains.",
                    nameof(rows));
            }

            claimants.Add(row);
        }

        PlanRow?[] claimedBy = AssignOccurrences(actions, claimants, out PlanRow? unmatched);
        if (unmatched is not null)
        {
            PlannedAction action = unmatched.Action!;
            throw new ArgumentException(
                $"A row names an action this plan does not contain, or names it more often than the plan "
                + $"contains it ({action.Kind}: {action.Description}). One row governs one execution, so a "
                + "second row for the same occurrence is ambiguous and a row from another plan is foreign.",
                nameof(rows));
        }

        for (int i = 0; i < actions.Count; i++)
        {
            if (claimedBy[i] is null && !vetoed.Contains(actions[i]))
            {
                throw new ArgumentException(
                    $"No row describes action #{i} ({actions[i].Kind}: {actions[i].Description}) of plan "
                    + $"'{source.Title}'. Composing anyway would authorize a plan the user was never shown — "
                    + "including one whose protective step has silently disappeared.",
                    nameof(rows));
            }
        }

        var survivors = new List<PlannedAction>(actions.Count);
        for (int i = 0; i < actions.Count; i++)
        {
            if (vetoed.Contains(actions[i]))
                continue; // Blocked dominates: an included alias cannot resurrect this occurrence.

            if (claimedBy[i] is { Selection: RowSelection.Required or RowSelection.OptionalIncluded })
                survivors.Add(actions[i]);
        }

        return new OperationPlan(source.Title, source.ModuleName, survivors, source.CreatedAtUtc);
    }

    /// <summary>
    /// Maps each claiming row onto one source occurrence: the n-th row naming an action takes that action's
    /// n-th occurrence, so two rows for one repeated instance govern one execution each. Returns the row that
    /// governs each source index, or null where no row claimed it. The first row that finds no unclaimed
    /// occurrence is reported through <paramref name="unmatched"/>; the caller owns the refusal, so this method
    /// stays a pure mapping and the composition contract has one voice.
    /// </summary>
    private static PlanRow?[] AssignOccurrences(
        IReadOnlyList<PlannedAction> actions,
        List<PlanRow> claimants,
        out PlanRow? unmatched)
    {
        unmatched = null;
        var unclaimed = new Dictionary<PlannedAction, Queue<int>>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < actions.Count; i++)
        {
            if (!unclaimed.TryGetValue(actions[i], out Queue<int>? occurrences))
                unclaimed[actions[i]] = occurrences = new Queue<int>();
            occurrences.Enqueue(i);
        }

        var claimedBy = new PlanRow?[actions.Count];
        foreach (PlanRow row in claimants)
        {
            PlannedAction action = row.Action!;
            if (!unclaimed.TryGetValue(action, out Queue<int>? occurrences) || occurrences.Count == 0)
            {
                unmatched = row;
                return claimedBy;
            }

            claimedBy[occurrences.Dequeue()] = row;
        }

        return claimedBy;
    }
}

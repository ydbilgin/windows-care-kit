using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Module.Uninstall.ViewModels;

/// <summary>
/// One staged removal: the plan the gate will show, and the rows it will show it with.
/// <para>
/// The two are built together on purpose. <see cref="PlanSelection.Subset"/> refuses to compose a plan whose
/// actions are not all named by rows (SPEC §2.0 rule 2), so a plan and a row list produced by two different
/// code paths can drift into a refusal — or worse, into a plan the user was never shown.
/// </para>
/// </summary>
/// <param name="Plan">Every action removal could run, in EXECUTION order: the restore point first, the vendor
/// uninstaller next, the leftovers last. Order is an execution-safety property and is never the view's.</param>
/// <param name="Rows">Every row the gate renders, required and blocked alike, in the order it renders them.
/// This is what is handed to <see cref="PlanSelection.Subset"/>.</param>
/// <param name="LeftoverRows">The vetoable leftover rows only — what the detail rail previews.</param>
/// <param name="BlockedRows">The <c>WON'T RUN</c> rows: shared and gate-protected leftovers, shown inline
/// with their reason and never executed.</param>
public sealed record RemovalStaging(
    OperationPlan Plan,
    IReadOnlyList<PlanRow> Rows,
    IReadOnlyList<PlanRow> LeftoverRows,
    IReadOnlyList<PlanRow> BlockedRows);

/// <summary>
/// Composes the ONE removal plan behind the Uninstall screen's single destructive door: an optional
/// protective restore point, the vendor's own uninstaller, and the program-owned leftovers — with the rows
/// that describe them.
/// <para>
/// It replaces the 4-beat wizard's two separate approval doors. The user now sees the vendor step and the
/// leftovers in the SAME list, at the same time, before anything runs: the vendor step and the restore point
/// carry the Required lock badge and cannot be unchecked, each leftover carries a real veto checkbox and
/// starts UNCHECKED, and each shared or gate-protected item is present as a <c>WON'T RUN</c> row stating its
/// reason (SPEC §3 M3, decision §2.5-9).
/// </para>
/// <para>
/// The ProgramOwned-only invariant is still enforced by <see cref="LeftoverPlanBuilder"/>, which throws
/// rather than silently dropping a shared or protected action. This type does not re-implement that check —
/// it feeds the builder and lets the exception surface, so there is exactly one guard to trust.
/// </para>
/// </summary>
public sealed class RemovalPlanComposer
{
    private readonly I18n _i18n;
    private readonly LeftoverPlanBuilder _leftoverPlans = new();

    public RemovalPlanComposer(I18n i18n) => _i18n = i18n ?? throw new ArgumentNullException(nameof(i18n));

    /// <summary>
    /// Builds the staging for <paramref name="app"/>, or null when there is nothing to run at all (no usable
    /// vendor uninstaller AND no program-owned leftover). A null result is the honest answer to "what would
    /// removal do?" — it is not an empty plan the user could still approve.
    /// </summary>
    /// <param name="app">The selected desktop program.</param>
    /// <param name="scan">The leftover scan, or null when the user has not run one yet.</param>
    /// <param name="withRestorePoint">
    /// True to prepend a protective System Restore point. The caller decides this from the capability probe
    /// AND the user's choice; this type does not probe, so it stays pure and host-testable.
    /// </param>
    /// <param name="utc">The plan timestamp.</param>
    /// <exception cref="LeftoverPlanBuildException">
    /// A non-ProgramOwned candidate reached the leftover plan builder. Surfaced, never swallowed: it means the
    /// classification barrier was bypassed, which the caller must show rather than quietly work around.
    /// </exception>
    public RemovalStaging? Compose(InstalledApp app, LeftoverScanResult? scan, bool withRestorePoint, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(app);

        OperationPlan? official = OfficialUninstallerPlanner.Build(app, utc);
        IReadOnlyList<PlannedAction> leftovers = BuildLeftoverActions(app, scan, utc);

        bool hasVendorStep = official is { IsEmpty: false };
        if (!hasVendorStep && leftovers.Count == 0)
            return null;

        var actions = new List<PlannedAction>(leftovers.Count + 2);
        var rows = new List<PlanRow>(leftovers.Count + 2);

        // The restore point rides with the VENDOR step only. It is the rollback layer for the one action that
        // has no undo; prepending it to a leftovers-only plan would demand elevation and a snapshot for work
        // the Recycle Bin and the .reg backup already cover.
        if (withRestorePoint && hasVendorStep)
        {
            var restorePoint = new CreateRestorePointAction
            {
                RestorePointName = _i18n.Format("uninstall.removal.restorePoint.name", app.DisplayName),
                Description = $"Create a System Restore point before uninstalling {app.DisplayName}",
                Reason = "Protective rollback layer co-staged with the official uninstaller (UI decision §5)",
                Risk = RiskLevel.Info,
                Undo = UndoCapability.None,
            };
            actions.Add(restorePoint);
            rows.Add(PlanRow.FromAction(restorePoint, _i18n));
        }

        if (official is not null)
        {
            foreach (PlannedAction action in official.Actions)
            {
                actions.Add(action);
                // Required, never vetoable: the vendor uninstaller IS the removal. A checkbox on it would
                // offer a choice between "remove this app" and "remove nothing but its leftovers", which the
                // primary button already answers.
                rows.Add(PlanRow.FromAction(action, _i18n));
            }
        }

        var leftoverRows = new List<PlanRow>(leftovers.Count);
        foreach (PlannedAction action in leftovers)
        {
            actions.Add(action);
            // Vetoable and UNCHECKED: nothing non-curated is pre-checked (decision §2.5-7). FromAction with a
            // veto requested lands on OptionalExcluded, so this is the default rather than a later reset.
            PlanRow row = PlanRow.FromAction(action, _i18n, isVetoable: true);
            leftoverRows.Add(row);
            rows.Add(row);
        }

        IReadOnlyList<PlanRow> blocked = BuildBlockedRows(scan);
        rows.AddRange(blocked);

        var plan = new OperationPlan($"Remove {app.DisplayName}", "uninstall", actions, utc);
        return new RemovalStaging(plan, rows, leftoverRows, blocked);
    }

    /// <summary>
    /// The program-owned leftover actions, taken through <see cref="LeftoverPlanBuilder"/> with every
    /// selectable candidate marked selected. The per-item veto has moved to the gate, so the builder is no
    /// longer where the user's choice is applied — but it is still where the ProgramOwned-only invariant and
    /// the target-signature de-duplication live, and routing around it would drop both.
    /// </summary>
    private IReadOnlyList<PlannedAction> BuildLeftoverActions(
        InstalledApp app, LeftoverScanResult? scan, DateTime utc)
    {
        if (scan is null)
            return Array.Empty<PlannedAction>();

        // OR, never assignment. Offering every selectable candidate is what fills the gate; keeping a
        // candidate that ALREADY claims to be selected is what keeps the builder's guard alive. Rewriting
        // Selected from Selectable would make the two agree by construction, so a forged non-ProgramOwned
        // candidate would be silently dropped instead of throwing — the defence-in-depth check would still
        // compile, still be tested in isolation, and never be reachable from the only caller that matters.
        var offered = scan.Candidates
            .Select(candidate => candidate with { Selected = candidate.Selected || candidate.Selectable })
            .ToList();

        return _leftoverPlans.Build(app, offered, utc).Actions;
    }

    /// <summary>
    /// The <c>WON'T RUN</c> rows: everything the classifier called Shared and everything the gate refused.
    /// They are rendered in the SAME list as the required and optional rows, so exclusion is visible next to
    /// what remains (decision §2.5-9) — and because <see cref="PlanRow.FromSkipped"/> produces
    /// <see cref="RowSelection.Blocked"/>, they can only ever subtract from the composed plan.
    /// </summary>
    private IReadOnlyList<PlanRow> BuildBlockedRows(LeftoverScanResult? scan)
    {
        if (scan is null)
            return Array.Empty<PlanRow>();

        return scan.Candidates
            .Where(candidate => candidate.Classification != LeftoverClassification.ProgramOwned)
            .Select(candidate => PlanRow.FromSkipped(candidate.Action, BlockedReason(candidate), _i18n))
            .ToArray();
    }

    private string BlockedReason(LeftoverCandidate candidate)
        => candidate.Classification == LeftoverClassification.Protected
            ? _i18n.Format("uninstall.removal.sub.protected", candidate.GateReason)
            : _i18n["uninstall.removal.sub.shared"];
}

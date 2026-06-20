using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>An action the gate refused, surfaced to the user so the report is honest about what was skipped.</summary>
public sealed record SkippedAction(PlannedAction Action, string Reason);

/// <summary>
/// The output of a read-only leftover scan. The load-bearing safety invariant (spec §6, PR-3 A) lives HERE:
///
/// <list type="bullet">
/// <item><see cref="Plan"/> is the DELETABLE plan — it contains ONLY <c>ProgramOwned</c> actions. Shared and
/// Protected candidates are excluded, so the plan that flows to <c>UninstallViewModel</c> → stage → hash →
/// <c>GatedExecutor</c> can never carry a Shared (vendor parent) or Protected key. This is the live guard;
/// it does not depend on the probe happening not to emit Shared candidates.</item>
/// <item><see cref="Candidates"/> is the FULL classified candidate list (ProgramOwned + Shared + Protected),
/// surfaced read-only for PR-4's 3-tier wizard UI. It is presentation only — it is NEVER executed.</item>
/// <item><see cref="Skipped"/> is what the gate refused (Protected), kept for the honest "skipped" report.</item>
/// </list>
/// </summary>
public sealed record LeftoverScanResult(
    OperationPlan Plan,
    IReadOnlyList<SkippedAction> Skipped,
    IReadOnlyList<LeftoverCandidate> Candidates);

/// <summary>
/// Turns the candidate leftovers found by an <see cref="ILeftoverProbe"/> into a typed, risk-classified
/// dry-run, then CLASSIFIES each gate-allowed candidate for ownership (<see cref="LeftoverClassifier"/>) and
/// emits a DELETABLE <see cref="OperationPlan"/> containing ONLY ProgramOwned actions (spec §6, PR-3 A). Every
/// candidate is first passed through the <see cref="ISafetyGate"/>: protected ones never enter the plan — they
/// are reported as <see cref="SkippedAction"/> instead. This scan is read-only; nothing is deleted (spec §1.1).
///
/// HONESTY (spec §6): the gate only re-blocks Protected, NOT Shared. The barrier that keeps a non-protected
/// Shared (vendor parent) key out of the deletable plan is THIS classification step plus the
/// <see cref="LeftoverPlanBuilder"/> invariant — not the gate.
/// </summary>
public sealed class LeftoverScanner
{
    private readonly ILeftoverProbe _probe;
    private readonly ISafetyGate _gate;
    private readonly LeftoverClassifier _classifier = new();

    public LeftoverScanner(ILeftoverProbe probe, ISafetyGate gate)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public LeftoverScanResult Scan(InstalledApp app, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(app);

        var candidates = new List<PlannedAction>();

        foreach (var d in _probe.FindLeftoverDirectories(app))
        {
            candidates.Add(new FileDeleteAction
            {
                Path = d.Path,
                ToRecycleBin = true,
                Description = $"Delete leftover folder: {d.Path}",
                Reason = d.Note,
                Risk = RiskLevel.Low,
                Undo = UndoCapability.Full, // recycle bin
            });
        }

        foreach (var k in _probe.FindLeftoverRegistryKeys(app))
        {
            candidates.Add(new RegistryDeleteAction
            {
                Hive = k.Hive,
                SubKeyPath = k.SubKeyPath,
                View = k.View,
                Description = $"Delete leftover registry key: {k.Hive}\\{k.SubKeyPath}",
                Reason = k.Note,
                Risk = RiskLevel.Medium,
                Undo = UndoCapability.Partial, // .reg export before delete
            });
        }

        foreach (var s in _probe.FindRelatedServices(app))
        {
            candidates.Add(new ServiceDeleteAction
            {
                ServiceName = s.ServiceName,
                Operation = ServiceOperation.Stop,
                // Carry the probe's correlation evidence so the classifier can verify the image path is
                // actually under the install directory (otherwise the service is Shared, spec §6 / PR-3 B).
                ImagePath = s.ImagePath,
                Description = $"Stop related service: {s.ServiceName}",
                Reason = s.Note,
                Risk = RiskLevel.High,
                Undo = UndoCapability.Partial,
            });
        }

        foreach (var t in _probe.FindRelatedTasks(app))
        {
            candidates.Add(new TaskDeleteAction
            {
                TaskPath = t.TaskPath,
                Operation = TaskOperation.Disable,
                Description = $"Disable related scheduled task: {t.TaskPath}",
                Reason = t.Note,
                Risk = RiskLevel.Medium,
                Undo = UndoCapability.Partial,
            });
        }

        var allowed = new List<PlannedAction>();
        var skipped = new List<SkippedAction>();
        foreach (var action in candidates)
        {
            var verdict = _gate.Evaluate(action);
            if (verdict.Allowed)
                allowed.Add(action);
            else
                skipped.Add(new SkippedAction(action, verdict.Reason));
        }

        // Classify ownership for every gate-allowed + gate-skipped candidate. The FULL classified list is
        // surfaced (read-only) for PR-4's 3-tier UI; the DELETABLE plan below is filtered to ProgramOwned only.
        IReadOnlyList<LeftoverCandidate> classified = _classifier.Classify(app, allowed, skipped);

        // The load-bearing invariant (spec §6 PR-3 A): the plan that gets staged/executed contains ONLY
        // ProgramOwned actions. Shared/Protected are excluded here, so they can never reach the GatedExecutor —
        // this holds TODAY even if the probe is later widened to emit more Shared candidates.
        var deletable = classified
            .Where(c => c.Classification == LeftoverClassification.ProgramOwned)
            .Select(c => c.Action)
            .ToList();

        var plan = new OperationPlan($"Clean up leftovers of {app.DisplayName}", "uninstall", deletable, utc);
        return new LeftoverScanResult(plan, skipped, classified);
    }
}

using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>An action the gate refused, surfaced to the user so the report is honest about what was skipped.</summary>
public sealed record SkippedAction(PlannedAction Action, string Reason);

/// <summary>The output of a read-only leftover scan: a gate-approved dry-run plan plus what was skipped.</summary>
public sealed record LeftoverScanResult(OperationPlan Plan, IReadOnlyList<SkippedAction> Skipped);

/// <summary>
/// Turns the candidate leftovers found by an <see cref="ILeftoverProbe"/> into a typed, risk-classified
/// dry-run <see cref="OperationPlan"/>. Every candidate is passed through the <see cref="ISafetyGate"/>:
/// protected ones never enter the plan — they are reported as <see cref="SkippedAction"/> instead. This
/// scan is read-only; nothing is deleted (spec §1.1 PR1).
/// </summary>
public sealed class LeftoverScanner
{
    private readonly ILeftoverProbe _probe;
    private readonly ISafetyGate _gate;

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

        var plan = new OperationPlan($"Clean up leftovers of {app.DisplayName}", "uninstall", allowed, utc);
        return new LeftoverScanResult(plan, skipped);
    }
}

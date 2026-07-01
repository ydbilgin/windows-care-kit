using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Migration;

public sealed record RejectedRestoreUndoStep(RestoreUndoStep Step, string Reason);

public sealed record RestoreUndoActionBuildResult(
    OperationPlan Plan,
    IReadOnlyList<RejectedRestoreUndoStep> RejectedSteps)
{
    public IReadOnlyDictionary<string, string> ActionEntryIds { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Pure undo-action builder. It validates only path shape; disk provenance is checked at the IO edge.</summary>
public static class RestoreUndoActionBuilder
{
    public static RestoreUndoActionBuildResult Build(RestoreUndoPlan undoPlan, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(undoPlan);

        var actions = new List<PlannedAction>();
        var rejected = new List<RejectedRestoreUndoStep>();
        var actionEntryIds = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (RestoreUndoStep step in undoPlan.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.BakPath))
            {
                rejected.Add(new RejectedRestoreUndoStep(
                    step,
                    "Restore created this file; undo restores overwritten files only, so this file will remain."));
                continue;
            }

            if (!IsExpectedBakSibling(step.TargetPath, step.BakPath))
            {
                rejected.Add(new RejectedRestoreUndoStep(step, "backup path is not the expected target sibling"));
                continue;
            }

            var action = new RestoreMergeAction
            {
                Source = step.BakPath,
                Destination = step.TargetPath,
                CreateBak = false,
                Risk = RiskLevel.Medium,
                Undo = UndoCapability.None,
                Description = $"Undo restore of {step.EntryId}",
                Reason = "Restore the journaled .bak over the target through the gated file-restore path.",
            };
            actions.Add(action);
            actionEntryIds[action.Id] = step.EntryId;
        }

        return new RestoreUndoActionBuildResult(
            new OperationPlan("Undo migrated settings restore", "migration-restore-undo", actions, utc),
            rejected)
        {
            ActionEntryIds = actionEntryIds,
        };
    }

    public static bool IsExpectedBakSibling(string targetPath, string bakPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(bakPath))
            return false;

        try
        {
            string targetFull = Path.GetFullPath(targetPath);
            string bakFull = Path.GetFullPath(bakPath);
            string? targetDir = Path.GetDirectoryName(targetFull);
            string? bakDir = Path.GetDirectoryName(bakFull);

            return !string.IsNullOrWhiteSpace(targetDir)
                   && string.Equals(targetDir, bakDir, StringComparison.OrdinalIgnoreCase)
                   && bakFull.StartsWith(targetFull + ".bak.", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

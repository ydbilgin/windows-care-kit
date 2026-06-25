using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Migration;

public sealed record RejectedRestoreUndoStep(RestoreUndoStep Step, string Reason);

public sealed record RestoreUndoActionBuildResult(
    OperationPlan Plan,
    IReadOnlyList<RejectedRestoreUndoStep> RejectedSteps);

/// <summary>Pure undo-action builder. It validates only path shape; disk provenance is checked at the IO edge.</summary>
public static class RestoreUndoActionBuilder
{
    public static RestoreUndoActionBuildResult Build(RestoreUndoPlan undoPlan, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(undoPlan);

        var actions = new List<PlannedAction>();
        var rejected = new List<RejectedRestoreUndoStep>();

        foreach (RestoreUndoStep step in undoPlan.Steps)
        {
            if (!IsExpectedBakSibling(step.TargetPath, step.BakPath))
            {
                rejected.Add(new RejectedRestoreUndoStep(step, "backup path is not the expected target sibling"));
                continue;
            }

            actions.Add(new RestoreMergeAction
            {
                Source = step.BakPath,
                Destination = step.TargetPath,
                CreateBak = false,
                Risk = RiskLevel.Medium,
                Undo = UndoCapability.None,
                Description = $"Undo restore of {step.EntryId}",
                Reason = "Restore the journaled .bak over the target through the gated file-restore path.",
            });
        }

        return new RestoreUndoActionBuildResult(
            new OperationPlan("Undo migrated settings restore", "migration-restore-undo", actions, utc),
            rejected);
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

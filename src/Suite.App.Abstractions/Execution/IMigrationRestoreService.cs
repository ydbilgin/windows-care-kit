using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;

namespace WindowsCareKit.App.Execution;

public sealed record RestorePreviewResult(
    MigrationRestorePlanResult PlanResult,
    RestoreReport RestoreReport,
    string PlanHash);

public sealed record RestoreExecutionResult(
    MigrationRestorePlanResult PlanResult,
    PlanExecutionReport Execution,
    RestoreState State,
    RestoreReport RestoreReport,
    bool Authorized = true);

public sealed record RestoreUndoPreviewResult(
    RestoreUndoActionBuildResult BuildResult,
    IReadOnlyList<RejectedRestoreUndoStep> RejectedSteps,
    string PlanHash);

public sealed record RestoreUndoResult(
    RestoreUndoActionBuildResult BuildResult,
    PlanExecutionReport Execution,
    IReadOnlyList<RejectedRestoreUndoStep> RejectedSteps,
    RestoreState State,
    bool Authorized = true);

/// <summary>
/// The Abstractions-level restore port the Restore module consumes. Its result DTOs project the sanctioned
/// executor outcome onto <see cref="PlanExecutionReport"/> so the module never references Suite.Execution.
/// Narrow by design (ISP): only the four operations RestoreViewModel needs, with the exact argument sets it
/// passes today (the concrete service's optional install-manifest/run-token parameters are composition detail).
/// </summary>
public interface IMigrationRestoreService
{
    RestorePreviewResult Preview(
        MigrationRestoreManifest manifest, string packageDirectory, string stateDirectory, DateTime utc);

    RestoreExecutionResult Restore(
        MigrationRestoreManifest manifest, string packageDirectory, string stateDirectory, DateTime utc,
        string? approvedHash = null);

    RestoreUndoPreviewResult PreviewUndo(RestoreState state, DateTime utc);

    RestoreUndoResult Undo(
        RestoreState state, string stateDirectory, DateTime utc, string? approvedUndoHash = null);
}

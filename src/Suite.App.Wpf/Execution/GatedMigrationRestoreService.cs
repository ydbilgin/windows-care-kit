using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Execution;

namespace WindowsCareKit.App.Execution;

public sealed class GatedMigrationRestoreService(MigrationRestoreService inner) : IMigrationRestoreService
{
    public RestorePreviewResult Preview(
        MigrationRestoreManifest manifest, string packageDirectory, string stateDirectory, DateTime utc)
    {
        MigrationRestorePreviewResult r = inner.Preview(manifest, packageDirectory, stateDirectory, utc);
        return new RestorePreviewResult(r.PlanResult, r.RestoreReport, r.PlanHash);
    }

    public RestoreExecutionResult Restore(
        MigrationRestoreManifest manifest, string packageDirectory, string stateDirectory, DateTime utc,
        string? approvedHash = null)
    {
        MigrationRestoreExecutionResult r =
            inner.Restore(manifest, packageDirectory, stateDirectory, utc, approvedHash: approvedHash);
        return new RestoreExecutionResult(
            r.PlanResult, ExecutionReportMapper.ToPlanReport(r.Execution), r.State, r.RestoreReport, r.Authorized);
    }

    public RestoreUndoPreviewResult PreviewUndo(RestoreState state, DateTime utc)
    {
        MigrationRestoreUndoPreviewResult r = inner.PreviewUndo(state, utc);
        return new RestoreUndoPreviewResult(r.BuildResult, r.RejectedSteps, r.PlanHash);
    }

    public RestoreUndoResult Undo(
        RestoreState state, string stateDirectory, DateTime utc, string? approvedUndoHash = null)
    {
        MigrationRestoreUndoResult r = inner.Undo(state, stateDirectory, utc, approvedUndoHash);
        return new RestoreUndoResult(
            r.BuildResult, ExecutionReportMapper.ToPlanReport(r.Execution), r.RejectedSteps, r.State, r.Authorized);
    }
}

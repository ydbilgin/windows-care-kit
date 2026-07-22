using WindowsCareKit.Execution;

namespace WindowsCareKit.App.Execution;

/// <summary>
/// The single authoritative projection of a sanctioned <see cref="ExecutionReport"/> (Suite.Execution) onto
/// the Abstractions-level <see cref="PlanExecutionReport"/> port type. Owned by the composition root; used by
/// both GatedPlanExecutor and GatedMigrationRestoreService so no module ever sees a Suite.Execution type.
/// </summary>
internal static class ExecutionReportMapper
{
    public static PlanExecutionReport ToPlanReport(ExecutionReport report) => new(
        report.Authorized,
        report.PlanHash,
        report.Results
            .Select(r => new PlanActionResult(r.ActionId, r.Kind, ToPlanStatus(r.Status), r.Detail))
            .ToArray());

    public static PlanActionStatus ToPlanStatus(ActionStatus status) => status switch
    {
        ActionStatus.Done => PlanActionStatus.Done,
        ActionStatus.Skipped => PlanActionStatus.Skipped,
        ActionStatus.Blocked => PlanActionStatus.Blocked,
        ActionStatus.Failed => PlanActionStatus.Failed,
        ActionStatus.NotRun => PlanActionStatus.NotRun,
        _ => PlanActionStatus.Failed,
    };
}

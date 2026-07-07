using WindowsCareKit.Core.Planning;
using WindowsCareKit.Execution;

namespace WindowsCareKit.App.Execution;

internal sealed class GatedPlanExecutor(GatedExecutor executor) : IPlanExecutor
{
    public PlanExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash)
    {
        ExecutionReport report = executor.ExecuteWithReport(plan, approvedPlanHash);
        return new PlanExecutionReport(
            report.Authorized,
            report.PlanHash,
            report.Results
                .Select(r => new PlanActionResult(r.ActionId, r.Kind, MapStatus(r.Status), r.Detail))
                .ToArray());
    }

    private static PlanActionStatus MapStatus(ActionStatus status) => status switch
    {
        ActionStatus.Done => PlanActionStatus.Done,
        ActionStatus.Skipped => PlanActionStatus.Skipped,
        ActionStatus.Blocked => PlanActionStatus.Blocked,
        ActionStatus.Failed => PlanActionStatus.Failed,
        ActionStatus.NotRun => PlanActionStatus.NotRun,
        _ => PlanActionStatus.Failed,
    };
}

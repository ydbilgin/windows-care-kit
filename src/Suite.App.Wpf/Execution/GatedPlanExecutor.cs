using WindowsCareKit.Core.Planning;
using WindowsCareKit.Execution;

namespace WindowsCareKit.App.Execution;

internal sealed class GatedPlanExecutor(GatedExecutor executor) : IPlanExecutor
{
    public PlanExecutionSummary ExecuteWithSummary(OperationPlan plan, string approvedPlanHash)
    {
        ExecutionReport report = executor.ExecuteWithReport(plan, approvedPlanHash);
        int skippedOrNotRun = report.Results.Count(r => r.Status is ActionStatus.Skipped or ActionStatus.NotRun);
        return new PlanExecutionSummary(report.DoneCount, skippedOrNotRun, report.FailedCount);
    }
}

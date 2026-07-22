using WindowsCareKit.Core.Planning;
using WindowsCareKit.Execution;

namespace WindowsCareKit.App.Execution;

public sealed class GatedPlanExecutor(GatedExecutor executor) : IPlanExecutor
{
    public PlanExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash)
        => ExecutionReportMapper.ToPlanReport(executor.ExecuteWithReport(plan, approvedPlanHash));
}

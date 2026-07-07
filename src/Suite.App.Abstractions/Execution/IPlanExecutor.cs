using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.App.Execution;

public interface IPlanExecutor
{
    PlanExecutionSummary ExecuteWithSummary(OperationPlan plan, string approvedPlanHash);
}

public sealed record PlanExecutionSummary(
    int DoneCount,
    int SkippedOrNotRunCount,
    int FailedCount);

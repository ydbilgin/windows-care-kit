using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.App.Execution;

public interface IPlanExecutor
{
    PlanExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash);

    PlanExecutionSummary ExecuteWithSummary(OperationPlan plan, string approvedPlanHash)
        => ExecuteWithReport(plan, approvedPlanHash).Summary;
}

public sealed record PlanExecutionSummary(
    int DoneCount,
    int SkippedOrNotRunCount,
    int FailedCount);

public enum PlanActionStatus
{
    Done,
    Skipped,
    Blocked,
    Failed,
    NotRun,
}

public sealed record PlanActionResult(
    string ActionId,
    string Kind,
    PlanActionStatus Status,
    string Detail);

public sealed record PlanExecutionReport(
    bool Authorized,
    string PlanHash,
    IReadOnlyList<PlanActionResult> Results)
{
    public int DoneCount => Results.Count(r => r.Status == PlanActionStatus.Done);
    public int SkippedOrNotRunCount => Results.Count(r => r.Status is PlanActionStatus.Skipped or PlanActionStatus.NotRun);
    public int FailedCount => Results.Count(r => r.Status is PlanActionStatus.Failed or PlanActionStatus.Blocked);
    public PlanExecutionSummary Summary => new(DoneCount, SkippedOrNotRunCount, FailedCount);
}

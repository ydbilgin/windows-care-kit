using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Execution;

/// <summary>
/// Adapts the sanctioned <see cref="GatedExecutor"/> onto the Core <see cref="IBackupExecutor"/> seam that
/// <see cref="BackupRunner"/> drives. It runs the real, single execution path (<see cref="GatedExecutor.ExecuteWithReport"/>)
/// and maps the execution layer's <see cref="ExecutionReport"/>/<see cref="ActionStatus"/> onto the Core
/// projection (<see cref="BackupExecutionReport"/>/<see cref="BackupActionStatus"/>). This is the one place the
/// Core→Execution boundary is bridged for Backup, so the runner itself stays free of any Suite.Execution
/// dependency. No execution behavior changes — only the result shape is projected.
/// </summary>
public sealed class BackupExecutorAdapter : IBackupExecutor
{
    private readonly GatedExecutor _executor;

    public BackupExecutorAdapter(GatedExecutor executor)
        => _executor = executor ?? throw new ArgumentNullException(nameof(executor));

    /// <inheritdoc />
    public BackupExecutionReport Execute(OperationPlan plan, string approvedPlanHash)
    {
        ExecutionReport report = _executor.ExecuteWithReport(plan, approvedPlanHash);
        var results = new List<BackupActionResult>(report.Results.Count);
        foreach (ActionResult r in report.Results)
            results.Add(new BackupActionResult(r.ActionId, Map(r.Status), r.Detail)
            {
                CopyOutcomes = r.CopyOutcomes,
            });
        return new BackupExecutionReport(report.Authorized, results);
    }

    private static BackupActionStatus Map(ActionStatus status) => status switch
    {
        ActionStatus.Done => BackupActionStatus.Done,
        ActionStatus.Skipped => BackupActionStatus.Skipped,
        ActionStatus.Blocked => BackupActionStatus.Blocked,
        ActionStatus.Failed => BackupActionStatus.Failed,
        ActionStatus.NotRun => BackupActionStatus.NotRun,
        _ => BackupActionStatus.NotRun,
    };
}

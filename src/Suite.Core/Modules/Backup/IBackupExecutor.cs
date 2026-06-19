using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Modules.Backup;

/// <summary>
/// The terminal state of one backup action, as the Backup domain sees it. This is a Core-native projection
/// of the execution layer's per-action status: <see cref="BackupRunner"/> lives in Suite.Core and must not
/// depend on Suite.Execution's <c>ExecutionReport</c>/<c>ActionStatus</c>, so the WPF shell maps those onto
/// these values. The names/semantics match the execution layer 1:1 (behavior-preserving).
/// </summary>
public enum BackupActionStatus
{
    /// <summary>The copy completed successfully.</summary>
    Done,

    /// <summary>The gate re-blocked the action at execution time. It did not run.</summary>
    Blocked,

    /// <summary>The adapter threw while performing the copy.</summary>
    Failed,

    /// <summary>The action was never attempted (a prior action stopped the plan, or authorization failed).</summary>
    NotRun,
}

/// <summary>One per-action backup outcome, projected from the execution layer for <see cref="BackupRunner"/>.</summary>
/// <param name="ActionId">The <see cref="PlannedAction.Id"/> this result is for.</param>
/// <param name="Status">What happened to the action.</param>
/// <param name="Detail">Human-readable detail (gate reason, exception summary, …) — used to classify the skip reason.</param>
public sealed record BackupActionResult(string ActionId, BackupActionStatus Status, string Detail);

/// <summary>
/// The result of executing a backup plan, projected into Core terms. Mirrors the execution layer's
/// <c>ExecutionReport</c> shape (authorized flag + per-action results) without coupling Core to that layer.
/// </summary>
/// <param name="Authorized">False when the plan was refused (nothing ran).</param>
/// <param name="Results">One <see cref="BackupActionResult"/> per action, in plan order.</param>
public sealed record BackupExecutionReport(bool Authorized, IReadOnlyList<BackupActionResult> Results);

/// <summary>
/// The execution seam <see cref="BackupRunner"/> drives. Suite.Core declares it; the WPF shell adapts the
/// sanctioned <c>GatedExecutor.ExecuteWithReport</c> onto it (mapping <c>ExecutionReport</c> →
/// <see cref="BackupExecutionReport"/>). This keeps the runner a pure Core orchestrator with no Suite.Execution
/// dependency, while the single real execution path is unchanged.
/// </summary>
public interface IBackupExecutor
{
    /// <summary>Execute the authorized <paramref name="plan"/> (hash-checked) and return per-action results.</summary>
    BackupExecutionReport Execute(OperationPlan plan, string approvedPlanHash);
}

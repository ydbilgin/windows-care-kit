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

    /// <summary>The copy ran but deliberately skipped the source without writing any file bytes.</summary>
    Skipped,

    /// <summary>The gate re-blocked the action at execution time. It did not run.</summary>
    Blocked,

    /// <summary>The adapter threw while performing the copy.</summary>
    Failed,

    /// <summary>The action was never attempted (a prior action stopped the plan, or authorization failed).</summary>
    NotRun,
}

/// <summary>
/// The Core-native projection of the execution layer's failure category (NEW-06). <see cref="BackupRunner"/>
/// lives in Suite.Core and must not depend on Suite.Execution's <c>ExecutionFailureCode</c>; the WPF shell's
/// <c>BackupExecutorAdapter</c> maps those values onto these 1:1 so the runner classifies a skip from a typed
/// code instead of parsing exception text.
/// </summary>
public enum BackupFailureCode
{
    /// <summary>The action did not fail.</summary>
    None,

    /// <summary>The source/target did not exist at execution time.</summary>
    Missing,

    /// <summary>The path exceeded what the OS could handle even with long-path support.</summary>
    TooLong,

    /// <summary>Access refused (forbidden/secret source, destination reparse point, or unauthorized access).</summary>
    Forbidden,

    /// <summary>The source/destination was locked or in use.</summary>
    Locked,

    /// <summary>The action threw, but matched no known category.</summary>
    Unknown,
}

/// <summary>One per-action backup outcome, projected from the execution layer for <see cref="BackupRunner"/>.</summary>
/// <param name="ActionId">The <see cref="PlannedAction.Id"/> this result is for.</param>
/// <param name="Status">What happened to the action.</param>
/// <param name="Detail">Human-readable, display/diagnostic-only detail (gate reason, exception summary, …). The
/// skip reason is classified from the typed <see cref="BackupFailureCode"/> below, never by parsing this text.</param>
public sealed record BackupActionResult(string ActionId, BackupActionStatus Status, string Detail)
{
    /// <summary>Structured copy outcomes produced by the real copy adapter, when available.</summary>
    public IReadOnlyList<CopyFileOutcome> CopyOutcomes { get; init; } = Array.Empty<CopyFileOutcome>();

    /// <summary>The typed failure category when <see cref="Status"/> is <see cref="BackupActionStatus.Failed"/>; else None.</summary>
    public BackupFailureCode FailureCode { get; init; } = BackupFailureCode.None;
}

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

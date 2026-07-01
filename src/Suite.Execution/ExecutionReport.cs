using WindowsCareKit.Core.Modules.Backup;

namespace WindowsCareKit.Execution;

/// <summary>The terminal state of one action after the executor ran (or refused to run) it.</summary>
public enum ActionStatus
{
    /// <summary>The adapter completed the action successfully.</summary>
    Done,

    /// <summary>The adapter deliberately skipped the action without writing or mutating its target.</summary>
    Skipped,

    /// <summary>The gate re-evaluated the action as blocked at execution time (the world changed). It did not run.</summary>
    Blocked,

    /// <summary>The adapter threw while performing the action.</summary>
    Failed,

    /// <summary>The action was never attempted (a prior action stopped the plan, or authorization failed).</summary>
    NotRun,
}

/// <summary>The per-action outcome surfaced to the UI and recorded in the <c>ExecutionLog</c>.</summary>
/// <param name="ActionId">The <see cref="WindowsCareKit.Core.Planning.PlannedAction.Id"/> this result is for.</param>
/// <param name="Kind">The action's <see cref="WindowsCareKit.Core.Planning.PlannedAction.Kind"/> (e.g. <c>file.delete</c>).</param>
/// <param name="Status">What happened to the action.</param>
/// <param name="Detail">Human-readable detail (gate reason, exception summary, exit code, backup path, …). Redacted in the log.</param>
public sealed record ActionResult(string ActionId, string Kind, ActionStatus Status, string Detail)
{
    /// <summary>Structured copy outcomes when this result comes from a <c>CopyAction</c>.</summary>
    public IReadOnlyList<CopyFileOutcome> CopyOutcomes { get; init; } = Array.Empty<CopyFileOutcome>();
}

/// <summary>
/// The richer execution result the UI binds to. <see cref="GatedExecutor.Execute"/> wraps this and
/// returns the simple <c>ExecutionOutcome</c>; the UI calls <see cref="GatedExecutor.ExecuteWithReport"/>
/// to get the per-action breakdown.
/// </summary>
/// <param name="Authorized">False when <c>ExecutionAuthorizer</c> refused the plan (nothing ran).</param>
/// <param name="PlanHash">The plan hash computed at authorization time.</param>
/// <param name="Results">One <see cref="ActionResult"/> per action, in plan order.</param>
public sealed record ExecutionReport(
    bool Authorized,
    string PlanHash,
    IReadOnlyList<ActionResult> Results)
{
    /// <summary>True only when the plan was authorized and every action completed.</summary>
    public bool AllDone => Authorized && Results.All(r => r.Status == ActionStatus.Done);

    /// <summary>How many actions completed successfully.</summary>
    public int DoneCount => Results.Count(r => r.Status == ActionStatus.Done);

    /// <summary>How many actions were deliberately skipped without being treated as failures.</summary>
    public int SkippedCount => Results.Count(r => r.Status == ActionStatus.Skipped);

    /// <summary>How many actions were blocked or threw (i.e. genuine failures, not best-effort skips).</summary>
    public int FailedCount => Results.Count(r => r.Status is ActionStatus.Failed or ActionStatus.Blocked);
}

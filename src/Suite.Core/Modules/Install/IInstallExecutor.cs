using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Modules.Install;

/// <summary>
/// The terminal state of one install/restore action, as the Install domain sees it. This is a Core-native
/// projection of the execution layer's per-action status: <see cref="InstallRunner"/> lives in Suite.Core and
/// must not depend on Suite.Execution's <c>ExecutionReport</c>/<c>ActionStatus</c>, so the WPF shell maps those
/// onto these values. The names/semantics match the execution layer 1:1 (behavior-preserving). The execute path
/// itself is deferred to Step 4 — this seam is DECLARED here but not driven in the export slice.
/// </summary>
public enum InstallActionStatus
{
    /// <summary>The install/restore completed successfully.</summary>
    Done,

    /// <summary>The gate re-blocked the action at execution time. It did not run.</summary>
    Blocked,

    /// <summary>The adapter threw while performing the install/restore.</summary>
    Failed,

    /// <summary>The action was never attempted (a prior action stopped the plan, or authorization failed).</summary>
    NotRun,
}

/// <summary>One per-action install outcome, projected from the execution layer for <see cref="InstallRunner"/>.</summary>
/// <param name="ActionId">The <see cref="PlannedAction.Id"/> this result is for.</param>
/// <param name="Status">What happened to the action.</param>
/// <param name="Detail">Human-readable detail (gate reason, exception summary, …).</param>
public sealed record InstallActionResult(string ActionId, InstallActionStatus Status, string Detail);

/// <summary>
/// The result of executing an install/restore plan, projected into Core terms. Mirrors the execution layer's
/// <c>ExecutionReport</c> shape (authorized flag + per-action results) without coupling Core to that layer.
/// </summary>
/// <param name="Authorized">False when the plan was refused (nothing ran).</param>
/// <param name="Results">One <see cref="InstallActionResult"/> per action, in plan order.</param>
public sealed record InstallExecutionReport(bool Authorized, IReadOnlyList<InstallActionResult> Results);

/// <summary>
/// The execution seam <see cref="InstallRunner"/> WILL drive in Step 4. Suite.Core declares it; the WPF shell
/// will adapt the sanctioned <c>GatedExecutor.ExecuteWithReport</c> onto it (mapping <c>ExecutionReport</c> →
/// <see cref="InstallExecutionReport"/>). This keeps the runner a pure Core orchestrator with no Suite.Execution
/// dependency, while the single real execution path is unchanged. NOTE: in this EXPORT slice the seam is only
/// DECLARED — <see cref="InstallRunner.ExportPlan"/> never calls it (dry-run reads the plan + writes JSON).
/// </summary>
public interface IInstallExecutor
{
    /// <summary>Execute the authorized <paramref name="plan"/> (hash-checked) and return per-action results.</summary>
    InstallExecutionReport Execute(OperationPlan plan, string approvedPlanHash);
}

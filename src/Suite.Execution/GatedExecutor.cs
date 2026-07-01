using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution.Adapters;

namespace WindowsCareKit.Execution;

/// <summary>
/// The single execution entry point for the whole suite, and the only code that drives the destructive
/// adapters. It enforces the spec §3 pipeline exactly: authorize → (per action) re-evaluate the gate →
/// dispatch to the matching adapter → log every step. It fails closed: a refused authorization runs
/// nothing; a per-action block or failure stops the plan and marks the rest <see cref="ActionStatus.NotRun"/>.
/// The one controlled exception is the best-effort carve-out for recycle-bin junk deletes (§A.4):
/// a <see cref="FileDeleteAction"/> that is <see cref="RiskLevel.Low"/> + <see cref="UndoCapability.Full"/>
/// is continue-and-record on failure, so one locked temp file does not abort a cleanup sweep.
/// </summary>
public sealed class GatedExecutor : IExecutor
{
    private readonly ISafetyGate _gate;
    private readonly ExecutionLog _log;
    private readonly IFileDeleteAdapter _fileAdapter;
    private readonly IRegistryAdapter _registryAdapter;
    private readonly IServiceAdapter _serviceAdapter;
    private readonly ITaskAdapter _taskAdapter;
    private readonly IProcessAdapter _processAdapter;
    private readonly ICopyAdapter _copyAdapter;
    private readonly IRestorePointCreator _restorePointCreator;

    /// <param name="restorePointCreator">
    /// The protective <see cref="CreateRestorePointAction"/> sink (PR-5). Optional for backward compatibility:
    /// when null, a fail-closed default is used that THROWS if such an action is dispatched — so a plan can
    /// never silently skip the restore point it was promised. The production app injects the real Win32 creator.
    /// </param>
    public GatedExecutor(
        ISafetyGate gate,
        ExecutionLog log,
        IFileDeleteAdapter fileAdapter,
        IRegistryAdapter registryAdapter,
        IServiceAdapter serviceAdapter,
        ITaskAdapter taskAdapter,
        IProcessAdapter processAdapter,
        ICopyAdapter copyAdapter,
        IRestorePointCreator? restorePointCreator = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _fileAdapter = fileAdapter ?? throw new ArgumentNullException(nameof(fileAdapter));
        _registryAdapter = registryAdapter ?? throw new ArgumentNullException(nameof(registryAdapter));
        _serviceAdapter = serviceAdapter ?? throw new ArgumentNullException(nameof(serviceAdapter));
        _taskAdapter = taskAdapter ?? throw new ArgumentNullException(nameof(taskAdapter));
        _processAdapter = processAdapter ?? throw new ArgumentNullException(nameof(processAdapter));
        _copyAdapter = copyAdapter ?? throw new ArgumentNullException(nameof(copyAdapter));
        _restorePointCreator = restorePointCreator ?? new UnavailableRestorePointCreator();
    }

    /// <inheritdoc />
    public ExecutionOutcome Execute(OperationPlan plan, string approvedPlanHash)
    {
        ExecutionReport report = ExecuteWithReport(plan, approvedPlanHash);
        if (!report.Authorized)
            return new ExecutionOutcome(false, FirstDetailOr(report, "plan refused"));

        string reason = $"{report.DoneCount} done, {report.SkippedCount} skipped, {report.FailedCount} failed of {report.Results.Count}";
        return new ExecutionOutcome(true, reason);
    }

    /// <summary>
    /// The full execution with per-action results. <see cref="Execute"/> wraps this; the UI calls this
    /// directly to render the per-action report.
    /// </summary>
    public ExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // 1) Authorize FIRST. Nothing runs unless the gate re-validates the whole plan AND the hash matches.
        ExecutionAuthorization auth = ExecutionAuthorizer.Authorize(plan, approvedPlanHash, _gate);
        if (!auth.Authorized)
        {
            _log.Append("plan.refused", $"{plan.ModuleName}: {auth.Reason}", new Dictionary<string, string?>
            {
                ["module"] = plan.ModuleName,
                ["planId"] = plan.Id,
                ["planHash"] = auth.PlanHash,
                ["reason"] = auth.Reason,
            });
            // Every action is NotRun in a refusal.
            var notRun = plan.Actions
                .Select(a => new ActionResult(a.Id, a.Kind, ActionStatus.NotRun, auth.Reason))
                .ToArray();
            return new ExecutionReport(false, auth.PlanHash, notRun);
        }

        // 2) plan.start
        _log.Append("plan.start", $"{plan.ModuleName}: {plan.Title}", new Dictionary<string, string?>
        {
            ["module"] = plan.ModuleName,
            ["planId"] = plan.Id,
            ["planHash"] = auth.PlanHash,
            ["actionCount"] = plan.Actions.Count.ToString(),
        });

        var results = new List<ActionResult>(plan.Actions.Count);
        bool stopped = false;

        // 3) Each action, in plan order.
        for (int i = 0; i < plan.Actions.Count; i++)
        {
            PlannedAction action = plan.Actions[i];

            if (stopped)
            {
                results.Add(new ActionResult(action.Id, action.Kind, ActionStatus.NotRun, "a prior action stopped the plan"));
                continue;
            }

            // 3a) Re-evaluate the gate at the moment of execution (TOCTOU, defense in depth).
            SafetyVerdict verdict = _gate.Evaluate(action);
            _log.LogVerdict(action, verdict);

            // 3b) Blocked now → record + STOP the plan (the world changed since authorization).
            if (!verdict.Allowed)
            {
                results.Add(new ActionResult(action.Id, action.Kind, ActionStatus.Blocked, verdict.Reason));
                stopped = true;
                continue;
            }

            // 3d/3e) Dispatch + try/catch.
            try
            {
                ActionResult result = Dispatch(action);
                string eventName = result.Status == ActionStatus.Skipped ? "action.skipped" : "action.done";
                _log.Append(eventName, $"{action.Kind}: {action.Description}", ActionData(action));
                results.Add(result);
            }
            catch (Exception ex)
            {
                string detail = $"{ex.GetType().Name}: {ex.Message}";
                _log.Append("action.failed", $"{action.Kind}: {action.Description}", ActionData(action, ex));
                results.Add(new ActionResult(action.Id, action.Kind, ActionStatus.Failed, detail));

                // §A.4 best-effort carve-out: a recycle-bin junk delete (Low + Full) is continue-and-record;
                // every other action type / risk tier stops the plan (fail closed).
                if (!IsBestEffort(action))
                    stopped = true;
            }
        }

        // 4) plan.done with tallies.
        int done = results.Count(r => r.Status == ActionStatus.Done);
        int skipped = results.Count(r => r.Status == ActionStatus.Skipped);
        int failed = results.Count(r => r.Status is ActionStatus.Failed or ActionStatus.Blocked);
        int notRunCount = results.Count(r => r.Status == ActionStatus.NotRun);
        _log.Append("plan.done", $"{plan.ModuleName}: {done} done / {skipped} skipped / {failed} failed / {notRunCount} not run",
            new Dictionary<string, string?>
            {
                ["module"] = plan.ModuleName,
                ["planId"] = plan.Id,
                ["done"] = done.ToString(),
                ["skipped"] = skipped.ToString(),
                ["failed"] = failed.ToString(),
                ["notRun"] = notRunCount.ToString(),
            });

        return new ExecutionReport(true, auth.PlanHash, results);
    }

    /// <summary>
    /// The §A.4 carve-out: only a file delete EXPLICITLY flagged <see cref="FileDeleteAction.BestEffort"/>
    /// continues on failure. That flag is set solely by the junk-sweep scanner, so an uninstall leftover
    /// delete that happens to be Low+Full (recycle-bin) still fails closed — the carve-out is no longer
    /// inferred from the risk/undo tier (L2). Risk/Undo are still asserted as a defensive sanity check.
    /// </summary>
    private static bool IsBestEffort(PlannedAction action)
        => action is FileDeleteAction { BestEffort: true }
           && action.Risk == RiskLevel.Low
           && action.Undo == UndoCapability.Full;

    private ActionResult Dispatch(PlannedAction action)
    {
        switch (action)
        {
            case FileDeleteAction file:
                _fileAdapter.Delete(file);
                return Done(action);
            case RegistryDeleteAction reg:
                _registryAdapter.Delete(reg);
                return Done(action);
            case ServiceDeleteAction svc:
                _serviceAdapter.Apply(svc);
                return Done(action);
            case TaskDeleteAction task:
                _taskAdapter.Apply(task);
                return Done(action);
            case CommandAction cmd:
                _processAdapter.Run(cmd);
                return Done(action);
            case CopyAction copy:
                return CopyResult(copy, _copyAdapter.Copy(copy));
            case RestoreMergeAction merge:
                _copyAdapter.Merge(merge);
                return Done(action);
            case CreateRestorePointAction restorePoint:
                _restorePointCreator.Create(restorePoint);
                return Done(action);
            default:
                throw new NotSupportedException($"No adapter for action kind '{action.Kind}'.");
        }
    }

    private static ActionResult Done(PlannedAction action)
        => new(action.Id, action.Kind, ActionStatus.Done, "ok");

    private static ActionResult CopyResult(CopyAction action, CopyAdapterResult result)
    {
        var outcomes = new List<CopyFileOutcome>();
        if (result.CopiedAny)
        {
            string copiedDetail = $"ok ({result.CopiedFileCount} file(s), {result.CopiedByteCount} byte(s))";
            outcomes.Add(new CopyFileOutcome(action.Id, action.Source, action.Destination, true, null, copiedDetail));
        }

        foreach (CopySkippedItem skipped in result.Skipped)
            outcomes.Add(new CopyFileOutcome(action.Id, skipped.Source, skipped.Destination, false, skipped.Reason, skipped.Detail));

        if (outcomes.Count == 0)
            outcomes.Add(new CopyFileOutcome(action.Id, action.Source, action.Destination, false, CopySkipReason.Other, "no files copied"));

        if (result.CopiedAny)
        {
            string detail = result.Skipped.Count == 0
                ? outcomes[0].Detail
                : $"{outcomes[0].Detail}; {result.Skipped.Count} skipped";
            return new ActionResult(action.Id, action.Kind, ActionStatus.Done, detail) { CopyOutcomes = outcomes };
        }

        CopyFileOutcome firstSkip = outcomes.First(o => !o.Copied);
        string skippedDetail = result.Skipped.Count <= 1
            ? $"Skipped ({firstSkip.Reason}): {firstSkip.Detail}"
            : $"Skipped ({firstSkip.Reason}): {firstSkip.Detail}; {result.Skipped.Count} total skipped";
        return new ActionResult(action.Id, action.Kind, ActionStatus.Skipped, skippedDetail) { CopyOutcomes = outcomes };
    }

    private static Dictionary<string, string?> ActionData(PlannedAction action, Exception? ex = null)
    {
        var data = new Dictionary<string, string?>
        {
            ["actionId"] = action.Id,
            ["kind"] = action.Kind,
            ["target"] = action.TargetSignature(),
            ["risk"] = action.Risk.ToString(),
            ["undo"] = action.Undo.ToString(),
        };
        if (ex is not null)
        {
            data["exception"] = ex.GetType().Name;
            data["message"] = ex.Message;
        }
        return data;
    }

    private static string FirstDetailOr(ExecutionReport report, string fallback)
        => report.Results.Count > 0 ? report.Results[0].Detail : fallback;

    /// <summary>
    /// Fail-closed default when no <see cref="IRestorePointCreator"/> is wired: dispatching a
    /// <see cref="CreateRestorePointAction"/> THROWS, so a plan that staged a restore point can never run
    /// without one having been created (the executor's try/catch then records the failure and stops the plan).
    /// </summary>
    private sealed class UnavailableRestorePointCreator : IRestorePointCreator
    {
        public void Create(CreateRestorePointAction action)
            => throw new NotSupportedException("No IRestorePointCreator is wired into this executor.");
    }
}

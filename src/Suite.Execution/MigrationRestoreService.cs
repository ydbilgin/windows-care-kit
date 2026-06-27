using System.Security.Cryptography;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Execution;

public sealed record MigrationRestoreExecutionResult(
    MigrationRestorePlanResult PlanResult,
    ExecutionReport Execution,
    RestoreState State,
    RestoreReport RestoreReport,
    bool Authorized = true);

public sealed record MigrationRestorePreviewResult(
    MigrationRestorePlanResult PlanResult,
    RestoreReport RestoreReport,
    string PlanHash);

public sealed record MigrationRestoreUndoResult(
    RestoreUndoActionBuildResult BuildResult,
    ExecutionReport Execution,
    IReadOnlyList<RejectedRestoreUndoStep> RejectedSteps);

/// <summary>
/// Production migration restore seam. It performs reads, plan-building, sanctioned gated execution, and
/// checkpoint persistence only; config and .bak mutations remain inside GatedExecutor -> CopyAdapter.
/// </summary>
public sealed class MigrationRestoreService
{
    private readonly MigrationRestoreRunner _runner;
    private readonly GatedExecutor _executor;
    private readonly IRestoreStateStore _stateStore;

    public MigrationRestoreService(
        MigrationRestoreRunner runner,
        GatedExecutor executor,
        IRestoreStateStore stateStore)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public MigrationRestoreExecutionResult Restore(
        MigrationRestoreManifest manifest,
        string packageDirectory,
        string stateDirectory,
        DateTime utc,
        string? runToken = null,
        InstallManifest? installManifest = null,
        InstallPlanner? installPlanner = null,
        string? approvedHash = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        RestoreState state = _stateStore.Load(stateDirectory);
        MigrationRestorePlanResult planned = _runner.BuildPlan(
            manifest, packageDirectory, state, utc, installManifest, installPlanner);

        string token = SanitizeFileToken(string.IsNullOrWhiteSpace(runToken) ? utc.Ticks.ToString("x") : runToken!);
        MigrationRestorePlanResult withBaks = AssignBakPaths(planned, token);
        string planHash = withBaks.Plan.ComputeHash();
        if (approvedHash is not null && !string.Equals(planHash, approvedHash, StringComparison.Ordinal))
        {
            return new MigrationRestoreExecutionResult(
                withBaks,
                new ExecutionReport(false, planHash, Array.Empty<ActionResult>()),
                state,
                EmptyRestoreReport(),
                Authorized: false);
        }

        IReadOnlyDictionary<string, MigrationRestoreActionSnapshot> snapshots = Snapshot(withBaks.Plan);

        ExecutionReport report = _executor.ExecuteWithReport(withBaks.Plan, planHash);
        RestoreState updated = EnsureStarted(state, report.PlanHash, utc);
        updated = ApplyStatuses(updated, report, withBaks.ActionEntryIds, utc);
        updated = MigrationRestoreJournalRecorder.Record(
            withBaks.Plan,
            report.Results.Select(MapResult).ToArray(),
            withBaks.ActionEntryIds,
            snapshots,
            utc,
            updated);

        _stateStore.Save(stateDirectory, updated);
        return new MigrationRestoreExecutionResult(withBaks, report, updated, RestoreReport.FromPlan(withBaks));
    }

    public MigrationRestorePreviewResult Preview(
        MigrationRestoreManifest manifest,
        string packageDirectory,
        string stateDirectory,
        DateTime utc,
        InstallManifest? installManifest = null,
        InstallPlanner? installPlanner = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        RestoreState state = _stateStore.Load(stateDirectory);
        MigrationRestorePlanResult planned = _runner.BuildPlan(
            manifest, packageDirectory, state, utc, installManifest, installPlanner);

        return new MigrationRestorePreviewResult(
            planned,
            RestoreReport.FromPlan(planned),
            planned.Plan.ComputeHash());
    }

    public MigrationRestoreUndoResult Undo(RestoreState state, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(state);

        RestoreUndoPlan undoPlan = RestoreJournal.BuildUndoPlan(state);
        (RestoreUndoPlan filtered, IReadOnlyList<RejectedRestoreUndoStep> shaRejected) =
            RejectWrongShaBackups(undoPlan, state);

        RestoreUndoActionBuildResult build = RestoreUndoActionBuilder.Build(filtered, utc);
        ExecutionReport report = _executor.ExecuteWithReport(build.Plan, build.Plan.ComputeHash());

        var rejected = new List<RejectedRestoreUndoStep>(shaRejected.Count + build.RejectedSteps.Count);
        rejected.AddRange(shaRejected);
        rejected.AddRange(build.RejectedSteps);

        return new MigrationRestoreUndoResult(build, report, rejected);
    }

    private static MigrationRestorePlanResult AssignBakPaths(MigrationRestorePlanResult planned, string runToken)
    {
        var actions = planned.Plan.Actions.Select(action =>
        {
            if (action is not RestoreMergeAction merge
                || !planned.ActionEntryIds.TryGetValue(merge.Id, out string? entryId))
            {
                return action;
            }

            string bakPath = $"{merge.Destination}.bak.{SanitizeFileToken(entryId)}.{runToken}";
            return merge with { BakPath = bakPath };
        }).ToArray();

        var plan = new OperationPlan(
            planned.Plan.Title,
            planned.Plan.ModuleName,
            actions,
            planned.Plan.CreatedAtUtc);

        return planned with { Plan = plan };
    }

    private static IReadOnlyDictionary<string, MigrationRestoreActionSnapshot> Snapshot(OperationPlan plan)
    {
        var snapshots = new Dictionary<string, MigrationRestoreActionSnapshot>(StringComparer.Ordinal);

        foreach (RestoreMergeAction action in plan.Actions.OfType<RestoreMergeAction>())
        {
            bool existed = File.Exists(action.Destination);
            snapshots[action.Id] = new MigrationRestoreActionSnapshot(
                action.Id,
                existed,
                existed ? Sha256File(action.Destination) : null,
                action.BakPath,
                File.Exists(action.Source) ? Sha256File(action.Source) : null);
        }

        return snapshots;
    }

    private static RestoreState EnsureStarted(RestoreState state, string planHash, DateTime utc)
        => string.IsNullOrEmpty(state.PlanHash)
            ? state with { PlanHash = planHash, StartedUtc = utc, UpdatedUtc = utc }
            : state with { UpdatedUtc = utc };

    private static RestoreState ApplyStatuses(
        RestoreState state,
        ExecutionReport report,
        IReadOnlyDictionary<string, string> actionEntryIds,
        DateTime utc)
    {
        RestoreState updated = state;
        foreach (ActionResult result in report.Results)
        {
            if (!actionEntryIds.TryGetValue(result.ActionId, out string? entryId))
                continue;

            RestoreEntryStatus status = result.Status switch
            {
                ActionStatus.Done => RestoreEntryStatus.Done,
                ActionStatus.Failed or ActionStatus.Blocked => RestoreEntryStatus.Failed,
                _ => RestoreEntryStatus.Pending,
            };
            updated = updated.With(entryId, status, utc);
        }

        return updated;
    }

    private static (RestoreUndoPlan Plan, IReadOnlyList<RejectedRestoreUndoStep> Rejected) RejectWrongShaBackups(
        RestoreUndoPlan undoPlan,
        RestoreState state)
    {
        var accepted = new List<RestoreUndoStep>();
        var rejected = new List<RejectedRestoreUndoStep>();

        foreach (RestoreUndoStep step in undoPlan.Steps)
        {
            RestoreJournalEntry? entry = state.Journal.FirstOrDefault(e =>
                string.Equals(e.EntryId, step.EntryId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.TargetPath, step.TargetPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.BakPath, step.BakPath, StringComparison.OrdinalIgnoreCase));

            if (entry is null || string.IsNullOrWhiteSpace(entry.ShaBefore))
            {
                rejected.Add(new RejectedRestoreUndoStep(step, "journal is missing the pre-restore sha"));
                continue;
            }

            if (File.Exists(step.BakPath))
            {
                string actual = Sha256File(step.BakPath);
                if (!string.Equals(actual, entry.ShaBefore, StringComparison.OrdinalIgnoreCase))
                {
                    rejected.Add(new RejectedRestoreUndoStep(step, "backup sha does not match the journal"));
                    continue;
                }
            }

            // If the .bak vanished after journaling, keep the step so CopyAdapter.Merge throws
            // FileNotFoundException visibly instead of turning the undo into a silent skip. The sha->copy
            // interval is a known same-user TOCTOU limitation and is outside this slice's threat model.
            accepted.Add(step);
        }

        return (new RestoreUndoPlan(accepted), rejected);
    }

    private static MigrationRestoreActionResult MapResult(ActionResult result)
        => new(result.ActionId, result.Status switch
        {
            ActionStatus.Done => MigrationRestoreActionStatus.Done,
            ActionStatus.Failed => MigrationRestoreActionStatus.Failed,
            ActionStatus.Blocked => MigrationRestoreActionStatus.Blocked,
            _ => MigrationRestoreActionStatus.NotRun,
        });

    private static RestoreReport EmptyRestoreReport()
        => new(
            Array.Empty<RestoreReportEntry>(),
            Array.Empty<RestoreReportEntry>(),
            Array.Empty<RestoreReportEntry>());

    private static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string SanitizeFileToken(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) || ch is '\\' or '/' ? '_' : ch).ToArray();
        return new string(chars);
    }
}

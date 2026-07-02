using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Backup;

/// <summary>
/// The result of a full backup run: the per-copy outcomes, the integrity rows, and whether the plan was
/// authorized. The WPF shell binds the rows to the UI; nothing here is WPF-specific.
/// </summary>
/// <param name="Authorized">False when the plan was refused (nothing ran, nothing written).</param>
/// <param name="CopyReport">The copied/skipped split derived from the executor's per-action results.</param>
/// <param name="Integrity">One integrity row per copied leaf file (empty when the run was refused).</param>
public sealed record BackupRunResult(
    bool Authorized,
    CopySkipReport CopyReport,
    IReadOnlyList<BackupIntegrity> Integrity);

/// <summary>
/// The pure, headless orchestrator for one backup run (spec §1.3). Given an authorized plan + hash it:
/// <list type="number">
/// <item>executes the plan through the <see cref="IBackupExecutor"/> seam (the sanctioned executor, adapted);</item>
/// <item>shapes the per-action results into a <see cref="CopySkipReport"/> (copied / skipped + reason);</item>
/// <item>walks the DESTINATION tree to build per-leaf integrity rows and writes <c>backup_integrity.json</c>;</item>
/// <item>writes <c>REPORT.md</c> + <c>MANUAL_TODO.md</c>.</item>
/// </list>
/// The copy-report shaping (<see cref="BuildCopyReport"/>) and skip classification (<see cref="ClassifySkip"/>)
/// moved here from the view-model verbatim, so the runner — not the UI — owns the testable core. It has no
/// Suite.Execution / WPF dependency: the executor is the Core <see cref="IBackupExecutor"/> port, and the
/// per-action status is the Core <see cref="BackupActionStatus"/> projection. Writes happen only when the plan
/// was authorized; both writers re-gate the payload root before touching disk.
/// </summary>
public sealed class BackupRunner
{
    private readonly IBackupExecutor _executor;
    private readonly IIntegrityWriter _integrityWriter;
    private readonly BackupReportWriter _reportWriter;
    private readonly ISafetyGate _gate;
    private readonly IFileSystem _fs;
    private readonly IHasher _hasher;
    private readonly IClock _clock;

    public BackupRunner(
        IBackupExecutor executor,
        IIntegrityWriter integrityWriter,
        BackupReportWriter reportWriter,
        ISafetyGate gate,
        IFileSystem fs,
        IHasher hasher,
        IClock clock)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _integrityWriter = integrityWriter ?? throw new ArgumentNullException(nameof(integrityWriter));
        _reportWriter = reportWriter ?? throw new ArgumentNullException(nameof(reportWriter));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Execute the approved plan, then build + write the integrity manifest and the two reports into
    /// <paramref name="payloadRoot"/>. When the executor refuses authorization, nothing is written and the
    /// result carries an empty copy report + no integrity rows.
    /// </summary>
    public BackupRunResult Run(
        BackupPlanResult planResult, string approvedPlanHash, string payloadRoot)
    {
        ArgumentNullException.ThrowIfNull(planResult);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);

        OperationPlan plan = planResult.Plan;
        BackupExecutionReport report = _executor.Execute(plan, approvedPlanHash);

        CopySkipReport copyReport = BuildCopyReport(plan, report);

        if (!report.Authorized)
            return new BackupRunResult(false, copyReport, Array.Empty<BackupIntegrity>());

        // Integrity: walk the destination tree for the leaves that actually landed, hash each, and write the
        // machine-readable manifest. This step ONLY reads + writes — it produces no new gated action.
        IReadOnlyList<BackupIntegrity> integrity =
            _integrityWriter.BuildIntegrity(copyReport, payloadRoot, _fs, _hasher, _clock);
        ReconcileCopyReportWithIntegrity(copyReport, integrity);
        _integrityWriter.WriteIntegrity(integrity, payloadRoot, _gate);

        // Human-readable reports (the writer re-gates the destination before writing).
        _reportWriter.WriteReports(planResult, copyReport, payloadRoot, _clock.UtcNow, _gate, integrity.Count);

        return new BackupRunResult(true, copyReport, integrity);
    }

    /// <summary>
    /// Join the plan's copy actions (by id) with the executor's per-action results into a copied/skipped report.
    /// Moved here from the view-model unchanged (behavior-preserving).
    /// </summary>
    public static CopySkipReport BuildCopyReport(OperationPlan plan, BackupExecutionReport report)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(report);

        var byId = new Dictionary<string, BackupActionResult>(StringComparer.Ordinal);
        foreach (BackupActionResult r in report.Results)
            byId[r.ActionId] = r;

        var outcomes = new List<CopyFileOutcome>();
        foreach (PlannedAction action in plan.Actions)
        {
            if (action is not CopyAction copy)
                continue;

            BackupActionResult? result = byId.TryGetValue(copy.Id, out BackupActionResult? r) ? r : null;
            if (result?.CopyOutcomes.Count > 0)
            {
                outcomes.AddRange(result.CopyOutcomes);
                continue;
            }

            bool copied = result?.Status == BackupActionStatus.Done;
            CopySkipReason? reason = copied ? null : ClassifySkip(result);
            outcomes.Add(new CopyFileOutcome(
                copy.Id, copy.Source, copy.Destination, copied, reason, result?.Detail ?? "not run"));
        }

        return new CopySkipReport(outcomes);
    }

    private static void ReconcileCopyReportWithIntegrity(
        CopySkipReport copyReport,
        IReadOnlyList<BackupIntegrity> integrity)
    {
        var copiedIds = new HashSet<string>(copyReport.Copied.Select(o => o.EntryId), StringComparer.Ordinal);
        var skippedOnlyIds = new HashSet<string>(
            copyReport.Skipped
                .Select(o => o.EntryId)
                .Where(id => !copiedIds.Contains(id)),
            StringComparer.Ordinal);

        foreach (BackupIntegrity row in integrity)
        {
            if (skippedOnlyIds.Contains(row.EntryId))
            {
                throw new InvalidOperationException(
                    $"integrity row '{row.DestinationRelativePath}' belongs to skipped copy entry '{row.EntryId}'.");
            }
        }
    }

    // The execution layer throws typed exceptions whose recorded detail is "{TypeName}: {message}". Match on
    // the stable type-name tokens (not a fragile English substring). These literals MUST stay in lockstep with
    // Suite.Execution.Adapters.{ForbiddenSourceException,DestinationReparseException}.TypeToken — Core cannot
    // reference that layer, so the names are duplicated as constants here.
    private const string ForbiddenSourceToken = "ForbiddenSourceException";
    private const string DestinationReparseToken = "DestinationReparseException";

    /// <summary>Map an executor failure detail to a <see cref="CopySkipReason"/>. Moved from the view-model unchanged.</summary>
    private static CopySkipReason ClassifySkip(BackupActionResult? result)
    {
        if (result is null)
            return CopySkipReason.Other;
        if (result.Status == BackupActionStatus.Blocked)
            return CopySkipReason.Blocked;
        if (result.Status == BackupActionStatus.Skipped)
            return CopySkipReason.Other;
        if (result.Status == BackupActionStatus.NotRun)
            return CopySkipReason.Other;

        string d = result.Detail;
        if (d.Contains("FileNotFound", StringComparison.OrdinalIgnoreCase) || d.Contains("DirectoryNotFound", StringComparison.OrdinalIgnoreCase))
            return CopySkipReason.Missing;
        if (d.Contains("PathTooLong", StringComparison.OrdinalIgnoreCase))
            return CopySkipReason.TooLong;
        if (d.Contains(ForbiddenSourceToken, StringComparison.Ordinal)
            || d.Contains(DestinationReparseToken, StringComparison.Ordinal)
            || d.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase))
            return CopySkipReason.Forbidden;
        if (d.Contains("IOException", StringComparison.OrdinalIgnoreCase) || d.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            return CopySkipReason.Locked;
        return CopySkipReason.Other;
    }
}

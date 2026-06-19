using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Execution.Adapters;

namespace WindowsCareKit.App.ViewModels;

/// <summary>
/// The Yedekle (Backup) view-model (spec §1.3). Flow: choose a payload folder OUTSIDE the app → load the
/// manifest → build a dry-run plan of <see cref="CopyAction"/>s (secrets are never copied) → preview with
/// <see cref="PlanRow"/> → explicit approve → execute via <see cref="IExecutor"/> → write/show
/// <c>RAPOR.md</c> + <c>MANUAL_TODO.md</c>. Nothing copies until the user approves the previewed plan and the
/// approved hash matches at execution time. There is no other execution path.
/// </summary>
public sealed class BackupViewModel : ObservableObject
{
    private readonly IManifestLoader _manifestLoader;
    private readonly BackupPlanner _planner;
    private readonly ISafetyGate _gate;
    private readonly IExecutor _executor;
    private readonly BackupReportWriter _reportWriter;

    private string _payloadDir = string.Empty;
    private bool _isBusy;
    private bool _isPreviewApproved;
    private string? _approvedHash;
    private BackupPlanResult? _planResult;
    private string _summary = string.Empty;
    private string _payloadWarning = string.Empty;

    public BackupViewModel(I18n i18n, IManifestLoader manifestLoader, BackupPlanner planner,
        ISafetyGate gate, IExecutor executor, BackupReportWriter reportWriter)
    {
        I18n = i18n;
        _manifestLoader = manifestLoader;
        _planner = planner;
        _gate = gate;
        _executor = executor;
        _reportWriter = reportWriter;

        BuildPlanCommand = new RelayCommand(async () => await BuildPlanAsync(), () => !IsBusy && HasPayloadDir);
        ApproveAndRunCommand = new RelayCommand(async () => await RunAsync(), () => CanRun);
    }

    public I18n I18n { get; }

    /// <summary>The dry-run preview rows (one per planned copy).</summary>
    public ObservableCollection<PlanRow> PlanRows { get; } = new();

    /// <summary>Items that need a manual step after the format (never-read secrets / manual-todo).</summary>
    public ObservableCollection<PlanRow> ManualRows { get; } = new();

    /// <summary>Items the planner excluded from the plan (disabled, gate-blocked, …).</summary>
    public ObservableCollection<PlanRow> SkippedRows { get; } = new();

    /// <summary>The post-execution per-copy outcomes (copied / skipped).</summary>
    public ObservableCollection<PlanRow> ResultRows { get; } = new();

    public ICommand BuildPlanCommand { get; }
    public ICommand ApproveAndRunCommand { get; }

    /// <summary>The chosen backup output folder; must be OUTSIDE the app folder (spec §1.3).</summary>
    public string PayloadDir
    {
        get => _payloadDir;
        set
        {
            if (SetField(ref _payloadDir, value))
            {
                OnPropertyChanged(nameof(HasPayloadDir));
                ResetPlan();
            }
        }
    }

    public bool HasPayloadDir => !string.IsNullOrWhiteSpace(_payloadDir);

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetField(ref _isBusy, value)) OnPropertyChanged(nameof(CanRun)); }
    }

    /// <summary>True once the user has approved the previewed plan; the approve button enables run.</summary>
    public bool IsPreviewApproved
    {
        get => _isPreviewApproved;
        set { if (SetField(ref _isPreviewApproved, value)) OnPropertyChanged(nameof(CanRun)); }
    }

    /// <summary>True when there is a non-empty, approved plan and we are not busy — gates the run command.</summary>
    public bool CanRun => !IsBusy && IsPreviewApproved && _planResult is { Plan.IsEmpty: false } && _approvedHash is not null;

    public bool HasPlan => _planResult is not null;
    public bool HasResults => ResultRows.Count > 0;

    /// <summary>Human summary of the last build or run (counts).</summary>
    public string Summary { get => _summary; private set => SetField(ref _summary, value); }

    /// <summary>Set when the chosen payload folder is invalid (inside the app folder, etc.).</summary>
    public string PayloadWarning { get => _payloadWarning; private set => SetField(ref _payloadWarning, value); }

    /// <summary>Build the dry-run copy plan from the manifest. Read-only; nothing is copied here.</summary>
    public async Task BuildPlanAsync()
    {
        if (!HasPayloadDir)
            return;

        IsBusy = true;
        ResetPlan();
        try
        {
            string payload = _payloadDir;
            string manifestsDir = Path.Combine(AppContext.BaseDirectory, "manifests");
            DateTime now = DateTime.UtcNow;

            (BackupPlanResult result, HashSet<string> wholeTreeIds) = await Task.Run(() =>
            {
                BackupManifest manifest = _manifestLoader.LoadFromDirectory(manifestsDir);
                BackupPlanResult r = _planner.BuildPlan(manifest, payload, now);

                // L7: probe each copy Source OFF-thread (Source is already env-expanded by the planner/loader).
                // A directory source means a recursive whole-tree copy → flag it so the preview row warns.
                var wholeTree = new HashSet<string>(StringComparer.Ordinal);
                foreach (PlannedAction a in r.Plan.Actions)
                {
                    if (a is CopyAction cp && !string.IsNullOrWhiteSpace(cp.Source) && Directory.Exists(cp.Source))
                        wholeTree.Add(cp.Id);
                }
                return (r, wholeTree);
            });

            _planResult = result;
            _approvedHash = result.Plan.ComputeHash();

            foreach (PlannedAction a in result.Plan.Actions)
                PlanRows.Add(PlanRow.FromAction(a, wholeTreeIds.Contains(a.Id)));
            foreach (BackupEntry e in result.ManualTodos)
                ManualRows.Add(ManualRow(e));
            foreach (BackupSkip s in result.Skipped)
                SkippedRows.Add(SkipRow(s));

            // Surface the "payload must be outside the app" warning when every copy was skipped for that reason.
            PayloadWarning = result.Plan.IsEmpty && result.Skipped.Any(s => s.Reason.Contains("outside the app", StringComparison.OrdinalIgnoreCase))
                ? I18n["backup.payloadOutsideRepo"]
                : string.Empty;

            Summary = I18n.Format("backup.report.summaryShort",
                result.Plan.Actions.Count, result.ManualTodos.Count, result.Skipped.Count);

            OnPropertyChanged(nameof(HasPlan));
            OnPropertyChanged(nameof(CanRun));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Authorize and execute the approved plan, then write and summarize the reports.</summary>
    public async Task RunAsync()
    {
        if (!CanRun || _planResult is null || _approvedHash is null)
            return;

        IsBusy = true;
        ResultRows.Clear();
        try
        {
            OperationPlan plan = _planResult.Plan;
            string hash = _approvedHash;
            string payload = _payloadDir;
            DateTime now = DateTime.UtcNow;
            BackupPlanResult planResult = _planResult;

            (ExecutionReport report, CopySkipReport copyReport) = await Task.Run(() =>
            {
                ExecutionReport r = _executor is GatedExecutor gated
                    ? gated.ExecuteWithReport(plan, hash)
                    : ToReport(_executor.Execute(plan, hash), plan);
                CopySkipReport copy = BuildCopyReport(plan, r);
                // Write RAPOR.md + MANUAL_TODO.md into the payload dir (outside the repo). The writer re-gates
                // the destination through the SafetyGate before touching disk (L9).
                if (r.Authorized)
                    _reportWriter.WriteReports(planResult, copy, payload, now, _gate);
                return (r, copy);
            });

            foreach (CopyFileOutcome o in copyReport.Outcomes)
                ResultRows.Add(ResultRow(o));

            Summary = report.Authorized
                ? I18n.Format("backup.report.summaryShort", copyReport.Copied.Count, planResult.ManualTodos.Count, copyReport.Skipped.Count)
                : (report.Results.Count > 0 ? report.Results[0].Detail : "refused");

            OnPropertyChanged(nameof(HasResults));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Join the plan's copy actions (by id) with the executor's per-action results into a skip-report.</summary>
    private static CopySkipReport BuildCopyReport(OperationPlan plan, ExecutionReport report)
    {
        var byId = report.Results.ToDictionary(r => r.ActionId, r => r);
        var outcomes = new List<CopyFileOutcome>();

        foreach (PlannedAction action in plan.Actions)
        {
            if (action is not CopyAction copy)
                continue;

            ActionResult? result = byId.TryGetValue(copy.Id, out ActionResult? r) ? r : null;
            bool copied = result?.Status == ActionStatus.Done;
            CopySkipReason? reason = copied ? null : ClassifySkip(result);
            outcomes.Add(new CopyFileOutcome(
                copy.Id, copy.Source, copy.Destination, copied, reason, result?.Detail ?? "not run"));
        }

        return new CopySkipReport(outcomes);
    }

    /// <summary>Map an executor failure detail to a <see cref="CopySkipReason"/> for the report.</summary>
    private static CopySkipReason ClassifySkip(ActionResult? result)
    {
        if (result is null)
            return CopySkipReason.Other;
        if (result.Status == ActionStatus.Blocked)
            return CopySkipReason.Blocked;
        if (result.Status == ActionStatus.NotRun)
            return CopySkipReason.Other;

        string d = result.Detail;
        if (d.Contains("FileNotFound", StringComparison.OrdinalIgnoreCase) || d.Contains("DirectoryNotFound", StringComparison.OrdinalIgnoreCase))
            return CopySkipReason.Missing;
        if (d.Contains("PathTooLong", StringComparison.OrdinalIgnoreCase))
            return CopySkipReason.TooLong;
        // The copy engine throws a typed ForbiddenSourceException for protected secret stores and a
        // DestinationReparseException when the destination component is a junction/symlink at the write
        // boundary; the executor records "{TypeName}: …", so match those stable type tokens (not a fragile message).
        if (d.Contains(ForbiddenSourceException.TypeToken, StringComparison.Ordinal)
            || d.Contains(DestinationReparseException.TypeToken, StringComparison.Ordinal)
            || d.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase))
            return CopySkipReason.Forbidden;
        if (d.Contains("IOException", StringComparison.OrdinalIgnoreCase) || d.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            return CopySkipReason.Locked;
        return CopySkipReason.Other;
    }

    /// <summary>Adapt a plain <see cref="ExecutionOutcome"/> to a report when the executor is not a <see cref="GatedExecutor"/> (test seam).</summary>
    private static ExecutionReport ToReport(ExecutionOutcome outcome, OperationPlan plan)
    {
        ActionStatus status = outcome.Ran ? ActionStatus.Done : ActionStatus.NotRun;
        var results = plan.Actions.Select(a => new ActionResult(a.Id, a.Kind, status, outcome.Reason)).ToArray();
        return new ExecutionReport(outcome.Ran, plan.ComputeHash(), results);
    }

    private void ResetPlan()
    {
        PlanRows.Clear();
        ManualRows.Clear();
        SkippedRows.Clear();
        ResultRows.Clear();
        _planResult = null;
        _approvedHash = null;
        IsPreviewApproved = false;
        Summary = string.Empty;
        PayloadWarning = string.Empty;
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CanRun));
    }

    private PlanRow ManualRow(BackupEntry e) => new()
    {
        Text = string.IsNullOrWhiteSpace(e.Description) ? e.Id : e.Description,
        RiskText = "MANUAL",
        RiskBrush = RiskVisuals.For(RiskLevel.High),
        Undo = string.Empty,
        Detail = e.UiWarning ?? I18n["backup.secret.manual"],
    };

    private static PlanRow SkipRow(BackupSkip s) => new()
    {
        Text = string.IsNullOrWhiteSpace(s.Entry.Description) ? s.Entry.Id : s.Entry.Description,
        RiskText = "SKIPPED",
        RiskBrush = RiskVisuals.For(RiskLevel.Info),
        Undo = string.Empty,
        Detail = s.Reason,
    };

    private static PlanRow ResultRow(CopyFileOutcome o) => new()
    {
        Text = o.Source,
        RiskText = o.Copied ? "COPIED" : "SKIPPED",
        RiskBrush = RiskVisuals.For(o.Copied ? RiskLevel.Low : RiskLevel.Critical),
        Undo = string.Empty,
        Detail = o.Copied ? o.Destination : $"{o.Reason}: {o.Detail}",
    };
}

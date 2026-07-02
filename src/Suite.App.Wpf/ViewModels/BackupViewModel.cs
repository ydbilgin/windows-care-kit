using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.App.ViewModels;

/// <summary>
/// The Yedekle (Backup) view-model (spec §1.3). Flow: choose a payload folder OUTSIDE the app → load the
/// manifest → build a dry-run plan of <see cref="CopyAction"/>s (secrets are never copied) → preview with
/// <see cref="PlanRow"/> → explicit approve → execute via <see cref="BackupRunner"/> → write/show
/// <c>REPORT.md</c> + <c>MANUAL_TODO.md</c>. Nothing copies until the user approves the previewed plan and the
/// approved hash matches at execution time. There is no other execution path.
/// </summary>
public sealed class BackupViewModel : ObservableObject
{
    private readonly IManifestLoader _manifestLoader;
    private readonly BackupPlanner _planner;
    private readonly BackupRunner _runner;

    private string _payloadDir = string.Empty;
    private bool _isBusy;
    private bool _isPreviewApproved;
    private string? _approvedHash;
    private BackupPlanResult? _planResult;
    private string _summary = string.Empty;
    private string _payloadWarning = string.Empty;

    public BackupViewModel(I18n i18n, IManifestLoader manifestLoader, BackupPlanner planner, BackupRunner runner)
    {
        I18n = i18n;
        _manifestLoader = manifestLoader;
        _planner = planner;
        _runner = runner;

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

    /// <summary>Authorize and execute the approved plan via <see cref="BackupRunner"/>, then summarize the results.</summary>
    public async Task RunAsync()
    {
        if (!CanRun || _planResult is null || _approvedHash is null)
            return;

        IsBusy = true;
        ResultRows.Clear();
        try
        {
            string hash = _approvedHash;
            string payload = _payloadDir;
            BackupPlanResult planResult = _planResult;

            // The whole headless chain — execute → build copy report → build+write integrity → write reports —
            // now lives in the Core BackupRunner; this view-model is a thin shell that runs it off-thread and
            // renders the result rows / summary.
            BackupRunResult result = await Task.Run(() => _runner.Run(planResult, hash, payload));

            CopySkipReport copyReport = result.CopyReport;
            foreach (CopyFileOutcome o in copyReport.Outcomes)
                ResultRows.Add(ResultRow(o));

            Summary = result.Authorized
                ? I18n.Format("backup.report.summaryShort", copyReport.Copied.Count, planResult.ManualTodos.Count, copyReport.Skipped.Count)
                : RefusedSummary(copyReport);

            OnPropertyChanged(nameof(HasResults));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The refused-run summary: the localized generic sentence PLUS the real reason of the first non-copied
    /// action (the gate-block / hash-mismatch / refusal detail the executor recorded). The runner reports a
    /// <see cref="CopyFileOutcome"/> per copy action even on refusal, each carrying the executor's auth reason
    /// in <see cref="CopyFileOutcome.Detail"/>; surfacing it keeps the UI from collapsing every refusal into the
    /// same opaque sentence (F2). Falls back to the bare generic sentence when no detail is available.
    /// </summary>
    private string RefusedSummary(CopySkipReport copyReport)
    {
        string generic = I18n["backup.report.refused"];
        CopyFileOutcome? first = copyReport.Skipped.FirstOrDefault();
        string? reason = first?.Detail;
        return string.IsNullOrWhiteSpace(reason) ? generic : $"{generic}: {reason}";
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

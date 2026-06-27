using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;

namespace WindowsCareKit.App.ViewModels;

/// <summary>
/// New-machine migration restore flow: load package manifest, preview through MigrationRestoreService,
/// require explicit approval of the preview hash, then run the same service path and surface undo honestly.
/// </summary>
public sealed class RestoreViewModel : ObservableObject
{
    private readonly MigrationRestoreService _restoreService;
    private readonly MigrationRestoreManifestStore _manifestStore;
    private readonly IRestoreStateStore _stateStore;

    private string _packageDir = string.Empty;
    private string _stateDir = DefaultStateDir();
    private bool _isBusy;
    private bool _isPreviewApproved;
    private bool _isUndoPreviewApproved;
    private string? _approvedHash;
    private string? _undoPreviewHash;
    private string? _approvedUndoHash;
    private MigrationRestorePlanResult? _previewPlan;
    private RestoreUndoActionBuildResult? _undoPreviewBuild;
    private MigrationRestoreManifest? _manifest;
    private RestoreState? _completedState;
    private RestoreReport? _lastReport;
    private bool _usePreviewDispositionLabels;
    private string _restoreSummary = string.Empty;
    private string _packageWarning = string.Empty;
    private string? _summaryKey;
    private object[] _summaryArgs = Array.Empty<object>();
    private string? _warningKey;
    private string _warningDetail = string.Empty;

    public RestoreViewModel(
        I18n i18n,
        MigrationRestoreService restoreService,
        MigrationRestoreManifestStore manifestStore,
        IRestoreStateStore stateStore)
    {
        I18n = i18n ?? throw new ArgumentNullException(nameof(i18n));
        _restoreService = restoreService ?? throw new ArgumentNullException(nameof(restoreService));
        _manifestStore = manifestStore ?? throw new ArgumentNullException(nameof(manifestStore));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        I18n.PropertyChanged += OnLanguageChanged;

        LoadAndPreviewCommand = new RelayCommand(async () => await LoadAndPreviewAsync(), () => CanPreview);
        RunRestoreCommand = new RelayCommand(async () => await RunRestoreAsync(), () => CanRunRestore);
        PreviewUndoCommand = new RelayCommand(async () => await PreviewUndoAsync(), () => CanPreviewUndo);
        UndoCommand = new RelayCommand(async () => await UndoAsync(), () => CanRunUndo);
    }

    public I18n I18n { get; }
    public ICommand LoadAndPreviewCommand { get; }
    public ICommand RunRestoreCommand { get; }
    public ICommand PreviewUndoCommand { get; }
    public ICommand UndoCommand { get; }

    public ObservableCollection<PlanRow> PlanRows { get; } = new();
    public ObservableCollection<PlanRow> SkippedRows { get; } = new();
    public ObservableCollection<PlanRow> ResultRows { get; } = new();
    public ObservableCollection<PlanRow> RestoredRows { get; } = new();
    public ObservableCollection<PlanRow> ReinstallEnqueuedRows { get; } = new();
    public ObservableCollection<PlanRow> ManualRows { get; } = new();
    public ObservableCollection<PlanRow> UndoRows { get; } = new();

    public string PackageDir
    {
        get => _packageDir;
        set
        {
            if (SetField(ref _packageDir, value))
            {
                OnPropertyChanged(nameof(HasPackageDir));
                ResetPreview();
            }
        }
    }

    public string StateDir
    {
        get => _stateDir;
        set
        {
            if (SetField(ref _stateDir, value))
            {
                OnPropertyChanged(nameof(HasStateDir));
                OnPropertyChanged(nameof(StateFilePath));
                ResetPreview();
            }
        }
    }

    public string StateFilePath => HasStateDir ? _stateStore.PathFor(_stateDir) : string.Empty;
    public bool HasPackageDir => !string.IsNullOrWhiteSpace(_packageDir);
    public bool HasStateDir => !string.IsNullOrWhiteSpace(_stateDir);
    public bool HasPreviewPlan => _previewPlan is not null;
    public bool HasPlanRows => PlanRows.Count > 0;
    public bool HasSkippedRows => SkippedRows.Count > 0;
    public bool HasResultRows => ResultRows.Count > 0;
    public bool HasRestoredRows => RestoredRows.Count > 0;
    public bool HasReinstallEnqueuedRows => ReinstallEnqueuedRows.Count > 0;
    public bool HasManualRows => ManualRows.Count > 0;
    public bool HasUndoRows => UndoRows.Count > 0;
    public string RestoredDispositionTitle => I18n[
        _usePreviewDispositionLabels
            ? "migration.restore.disposition.RestorePlanned"
            : "migration.restore.disposition.Restored"];
    public bool HasUndoCandidates =>
        _completedState?.Journal.Any(entry => !string.IsNullOrWhiteSpace(entry.BakPath)) == true;
    public bool CanPreview => !IsBusy && HasPackageDir && HasStateDir;

    public bool CanRunRestore =>
        !IsBusy
        && IsPreviewApproved
        && _manifest is not null
        && _previewPlan is { Plan.IsEmpty: false }
        && _approvedHash is not null;

    public bool CanPreviewUndo => !IsBusy && HasUndoCandidates;

    public bool CanRunUndo =>
        !IsBusy
        && IsUndoPreviewApproved
        && _completedState is not null
        && _undoPreviewBuild is not null
        && _approvedUndoHash is not null;

    public bool CanUndo => CanRunUndo;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                RaiseCommandState();
        }
    }

    public bool IsPreviewApproved
    {
        get => _isPreviewApproved;
        set
        {
            if (SetField(ref _isPreviewApproved, value))
                RaiseCommandState();
        }
    }

    public bool IsUndoPreviewApproved
    {
        get => _isUndoPreviewApproved;
        set
        {
            if (SetField(ref _isUndoPreviewApproved, value))
            {
                _approvedUndoHash = value ? _undoPreviewHash : null;
                RaiseCommandState();
            }
        }
    }

    public string RestoreSummary
    {
        get => _restoreSummary;
        private set => SetField(ref _restoreSummary, value);
    }

    public string PackageWarning
    {
        get => _packageWarning;
        private set => SetField(ref _packageWarning, value);
    }

    public async Task LoadAndPreviewAsync()
    {
        if (!CanPreview)
            return;

        IsBusy = true;
        ResetPreview();
        try
        {
            if (!TryNormalizeDirectory(_packageDir, out string packageDir)
                || !TryNormalizeDirectory(_stateDir, out string stateDir))
            {
                SetPackageWarning("migration.restore.invalidPackageWarning");
                return;
            }

            try
            {
                (MigrationRestoreManifest Manifest, MigrationRestorePreviewResult Preview) built =
                    await Task.Run(() =>
                    {
                        MigrationRestoreManifest manifest = _manifestStore.Load(packageDir);
                        MigrationRestorePreviewResult preview =
                            _restoreService.Preview(manifest, packageDir, stateDir, DateTime.UtcNow);
                        return (manifest, preview);
                    });

                _manifest = built.Manifest;
                _previewPlan = built.Preview.PlanResult;
                _approvedHash = built.Preview.PlanHash;
                _lastReport = built.Preview.RestoreReport;
                _usePreviewDispositionLabels = true;
                SetNormalizedPaths(packageDir, stateDir);

                foreach (PlannedAction action in built.Preview.PlanResult.Plan.Actions)
                    PlanRows.Add(PlanRow.FromAction(action));
                foreach (RestoreSkip skip in built.Preview.PlanResult.Skipped)
                    SkippedRows.Add(SkipRow(skip));
                PopulateDispositionRows(built.Preview.RestoreReport);

                SetSummary(
                    "migration.restore.previewSummary",
                    built.Preview.PlanResult.Plan.Actions.Count,
                    built.Preview.PlanResult.Skipped.Count,
                    built.Preview.RestoreReport.Restored.Count,
                    built.Preview.RestoreReport.ReinstallEnqueued.Count,
                    built.Preview.RestoreReport.Manual.Count);
            }
            catch (MigrationManifestException ex)
            {
                SetPackageWarning("migration.restore.noManifestWarning", ex.Message);
            }
        }
        finally
        {
            IsBusy = false;
            RaiseAllListState();
        }
    }

    public async Task RunRestoreAsync()
    {
        if (!CanRunRestore || _manifest is null || _approvedHash is null)
            return;

        IsBusy = true;
        ResultRows.Clear();
        UndoRows.Clear();
        ResetUndoPreview();
        try
        {
            MigrationRestoreManifest manifest = _manifest;
            string packageDir = _packageDir;
            string stateDir = _stateDir;
            string approvedHash = _approvedHash;

            // A fresh UtcNow is safe here: utc does not enter TargetSignature / OperationPlan.ComputeHash,
            // so the freshly-rebuilt plan still hashes to the approved hash and the gate cannot false-refuse.
            MigrationRestoreExecutionResult result = await Task.Run(() =>
                _restoreService.Restore(
                    manifest,
                    packageDir,
                    stateDir,
                    DateTime.UtcNow,
                    approvedHash: approvedHash));

            if (!result.Authorized)
            {
                _completedState = null;
                _lastReport = result.RestoreReport;
                _usePreviewDispositionLabels = false;
                PopulateDispositionRows(result.RestoreReport);
                SetSummary("migration.restore.refused");
                return;
            }

            foreach (ActionResult actionResult in result.Execution.Results)
            {
                PlannedAction? action = result.PlanResult.Plan.Actions
                    .FirstOrDefault(a => string.Equals(a.Id, actionResult.ActionId, StringComparison.Ordinal));
                ResultRows.Add(ResultRow(actionResult, action));
            }

            _completedState = result.State;
            _lastReport = result.RestoreReport;
            _usePreviewDispositionLabels = false;
            // A completed restore consumes its approval: drop it so the primary action disables
            // instead of re-running into a confusing fail-closed "refused" (the now-Done targets
            // would rebuild an empty plan whose hash no longer matches). Undo preview stays available
            // from the completed state's journal, while undo execution still needs its own approval.
            IsPreviewApproved = false;
            PopulateDispositionRows(result.RestoreReport);
            SetSummary(
                "migration.restore.resultSummary",
                result.Execution.DoneCount,
                result.Execution.FailedCount,
                result.Execution.Results.Count(r => r.Status == ActionStatus.NotRun),
                result.RestoreReport.Restored.Count,
                result.RestoreReport.ReinstallEnqueued.Count,
                result.RestoreReport.Manual.Count);
        }
        finally
        {
            IsBusy = false;
            RaiseAllListState();
        }
    }

    public async Task UndoAsync()
    {
        if (!CanRunUndo || _completedState is null || _approvedUndoHash is null)
            return;

        IsBusy = true;
        UndoRows.Clear();
        try
        {
            RestoreState state = _completedState;
            string approvedUndoHash = _approvedUndoHash;
            MigrationRestoreUndoResult undo = await Task.Run(() =>
                _restoreService.Undo(state, DateTime.UtcNow, approvedUndoHash));

            if (!undo.Authorized)
                return;

            foreach (ActionResult actionResult in undo.Execution.Results)
            {
                PlannedAction? action = undo.BuildResult.Plan.Actions
                    .FirstOrDefault(a => string.Equals(a.Id, actionResult.ActionId, StringComparison.Ordinal));
                UndoRows.Add(ResultRow(actionResult, action));
            }

            foreach (RejectedRestoreUndoStep rejected in undo.RejectedSteps)
                UndoRows.Add(RejectedUndoRow(rejected));

            SetSummary(
                "migration.restore.undoSummary",
                undo.Execution.DoneCount,
                undo.Execution.FailedCount + undo.RejectedSteps.Count);

            ResetUndoPreview(clearRows: false);
        }
        finally
        {
            IsBusy = false;
            RaiseAllListState();
        }
    }

    public async Task PreviewUndoAsync()
    {
        if (!CanPreviewUndo || _completedState is null)
            return;

        IsBusy = true;
        ResetUndoPreview();
        try
        {
            RestoreState state = _completedState;
            MigrationRestoreUndoPreviewResult preview = await Task.Run(() =>
                _restoreService.PreviewUndo(state, DateTime.UtcNow));

            _undoPreviewBuild = preview.BuildResult;
            _undoPreviewHash = preview.PlanHash;

            foreach (PlannedAction action in preview.BuildResult.Plan.Actions)
                UndoRows.Add(PlanRow.FromAction(action));
            foreach (RejectedRestoreUndoStep rejected in preview.RejectedSteps)
                UndoRows.Add(RejectedUndoRow(rejected));
        }
        finally
        {
            IsBusy = false;
            RaiseAllListState();
        }
    }

    private void ResetPreview()
    {
        PlanRows.Clear();
        SkippedRows.Clear();
        ResultRows.Clear();
        RestoredRows.Clear();
        ReinstallEnqueuedRows.Clear();
        ManualRows.Clear();
        UndoRows.Clear();
        _previewPlan = null;
        _undoPreviewBuild = null;
        _manifest = null;
        _approvedHash = null;
        _undoPreviewHash = null;
        _approvedUndoHash = null;
        _completedState = null;
        _lastReport = null;
        _usePreviewDispositionLabels = false;
        IsPreviewApproved = false;
        IsUndoPreviewApproved = false;
        ClearSummary();
        ClearPackageWarning();
        RaiseAllListState();
    }

    private void ResetUndoPreview(bool clearRows = true)
    {
        if (clearRows)
            UndoRows.Clear();
        _undoPreviewBuild = null;
        _undoPreviewHash = null;
        _approvedUndoHash = null;
        IsUndoPreviewApproved = false;
    }

    private void PopulateDispositionRows(RestoreReport report)
    {
        RestoredRows.Clear();
        ReinstallEnqueuedRows.Clear();
        ManualRows.Clear();

        foreach (RestoreReportEntry entry in report.Restored)
            RestoredRows.Add(ReportRow(entry));
        foreach (RestoreReportEntry entry in report.ReinstallEnqueued)
            ReinstallEnqueuedRows.Add(ReportRow(entry));
        foreach (RestoreReportEntry entry in report.Manual)
            ManualRows.Add(ReportRow(entry));
    }

    private PlanRow SkipRow(RestoreSkip skip)
    {
        RiskLevel risk = skip.Reason switch
        {
            RestoreSkipReason.AlreadyDone => RiskLevel.Info,
            RestoreSkipReason.GateBlocked or RestoreSkipReason.MachineLocked
                or RestoreSkipReason.RebindRejected or RestoreSkipReason.SourceMissing => RiskLevel.Critical,
            _ => RiskLevel.High,
        };

        return new PlanRow
        {
            Text = $"{skip.Target.RecipeId}: {skip.Target.RelativePath}",
            RiskText = I18n["migration.restore.status.skipped"],
            RiskBrush = RiskVisuals.For(risk),
            Undo = string.Empty,
            Detail = $"{skip.Reason}: {LocalizedNote(RestoreSkipNotes.HumanNote(skip))}",
        };
    }

    private PlanRow ReportRow(RestoreReportEntry entry)
    {
        RiskLevel risk = entry.Disposition switch
        {
            RestoreDisposition.Restored => RiskLevel.Low,
            RestoreDisposition.ReinstallEnqueued => RiskLevel.Medium,
            _ => RiskLevel.High,
        };

        return new PlanRow
        {
            Text = string.IsNullOrWhiteSpace(entry.RecipeId) ? entry.Id : entry.RecipeId,
            RiskText = I18n[DispositionKey(entry.Disposition)],
            RiskBrush = RiskVisuals.For(risk),
            Undo = string.Empty,
            Detail = $"{entry.Reason}: {LocalizedNote(entry.Note)}",
        };
    }

    private PlanRow ResultRow(ActionResult result, PlannedAction? action)
    {
        PlanRow row = action is null
            ? new PlanRow
            {
                Text = result.ActionId,
                RiskText = result.Status.ToString(),
                RiskBrush = RiskVisuals.For(RiskLevel.Info),
                Undo = string.Empty,
                Detail = result.Detail,
            }
            : PlanRow.FromAction(action);

        return new PlanRow
        {
            Text = row.Text,
            RiskText = I18n[$"migration.restore.status.{result.Status}"],
            RiskBrush = RiskVisuals.For(StatusRisk(result.Status)),
            Undo = row.Undo,
            Detail = result.Detail,
            IsElevated = row.IsElevated,
        };
    }

    private PlanRow RejectedUndoRow(RejectedRestoreUndoStep rejected) => new()
    {
        Text = rejected.Step.TargetPath,
        RiskText = I18n["migration.restore.status.rejected"],
        RiskBrush = RiskVisuals.For(RiskLevel.Critical),
        Undo = string.Empty,
        Detail = rejected.Reason,
    };

    private void SetNormalizedPaths(string packageDir, string stateDir)
    {
        if (!string.Equals(_packageDir, packageDir, StringComparison.Ordinal))
        {
            _packageDir = packageDir;
            OnPropertyChanged(nameof(PackageDir));
            OnPropertyChanged(nameof(HasPackageDir));
        }

        if (!string.Equals(_stateDir, stateDir, StringComparison.Ordinal))
        {
            _stateDir = stateDir;
            OnPropertyChanged(nameof(StateDir));
            OnPropertyChanged(nameof(HasStateDir));
            OnPropertyChanged(nameof(StateFilePath));
        }
    }

    private void SetSummary(string key, params object[] args)
    {
        _summaryKey = key;
        _summaryArgs = args;
        RestoreSummary = I18n.Format(key, args);
    }

    private void ClearSummary()
    {
        _summaryKey = null;
        _summaryArgs = Array.Empty<object>();
        RestoreSummary = string.Empty;
    }

    private void SetPackageWarning(string key, string detail = "")
    {
        _warningKey = key;
        _warningDetail = detail;
        PackageWarning = string.IsNullOrWhiteSpace(detail) ? I18n[key] : $"{I18n[key]}: {detail}";
    }

    private void ClearPackageWarning()
    {
        _warningKey = null;
        _warningDetail = string.Empty;
        PackageWarning = string.Empty;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_lastReport is not null)
            PopulateDispositionRows(_lastReport);
        OnPropertyChanged(nameof(RestoredDispositionTitle));
        if (_summaryKey is not null)
            RestoreSummary = I18n.Format(_summaryKey, _summaryArgs);
        if (_warningKey is not null)
            PackageWarning = string.IsNullOrWhiteSpace(_warningDetail)
                ? I18n[_warningKey]
                : $"{I18n[_warningKey]}: {_warningDetail}";
        RaiseAllListState();
    }

    private void RaiseCommandState()
    {
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanRunRestore));
        OnPropertyChanged(nameof(CanPreviewUndo));
        OnPropertyChanged(nameof(CanRunUndo));
        OnPropertyChanged(nameof(CanUndo));
        CommandManager.InvalidateRequerySuggested();
    }

    private void RaiseAllListState()
    {
        OnPropertyChanged(nameof(HasPreviewPlan));
        OnPropertyChanged(nameof(HasPlanRows));
        OnPropertyChanged(nameof(HasSkippedRows));
        OnPropertyChanged(nameof(HasResultRows));
        OnPropertyChanged(nameof(HasRestoredRows));
        OnPropertyChanged(nameof(HasReinstallEnqueuedRows));
        OnPropertyChanged(nameof(HasManualRows));
        OnPropertyChanged(nameof(HasUndoRows));
        OnPropertyChanged(nameof(RestoredDispositionTitle));
        OnPropertyChanged(nameof(HasUndoCandidates));
        RaiseCommandState();
    }

    private string LocalizedNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return string.Empty;

        if (note.StartsWith("EN/TR:", StringComparison.OrdinalIgnoreCase))
            return note["EN/TR:".Length..].Trim();

        int en = note.IndexOf("EN:", StringComparison.OrdinalIgnoreCase);
        int tr = note.IndexOf("TR:", StringComparison.OrdinalIgnoreCase);
        if (I18n.Culture == "tr" && tr >= 0)
            return note[(tr + 3)..].Trim();
        if (en >= 0)
        {
            int end = tr > en ? tr : note.Length;
            return note[(en + 3)..end].Trim();
        }

        return note;
    }

    private static RiskLevel StatusRisk(ActionStatus status) => status switch
    {
        ActionStatus.Done => RiskLevel.Low,
        ActionStatus.NotRun => RiskLevel.Info,
        _ => RiskLevel.Critical,
    };

    private string DispositionKey(RestoreDisposition disposition)
        => disposition == RestoreDisposition.Restored && _usePreviewDispositionLabels
            ? "migration.restore.disposition.RestorePlanned"
            : $"migration.restore.disposition.{disposition}";

    private static bool TryNormalizeDirectory(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            return !string.IsNullOrWhiteSpace(normalized);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string DefaultStateDir()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsCareKit",
            "restore-state");
}

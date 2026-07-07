using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.App.ViewModels;

/// <summary>One sign-in row in the auth panel: present/absent (probed by existence only — contents never read).</summary>
public sealed class AuthRow
{
    public required string Label { get; init; }
    public required bool Present { get; init; }
    public required string StatusText { get; init; }
}

/// <summary>
/// The Kur (Install/Restore) view-model (spec §1.4): loads the reinstall manifest, builds an ordered
/// restore plan of typed <see cref="CommandAction"/> (winget/npm) and <see cref="RestoreMergeAction"/>
/// (config) actions, previews it as a dry-run, and — only after an explicit approve — runs it through the
/// <see cref="IPlanExecutor"/>. It persists a <see cref="RestoreState"/> checkpoint after execution so a reboot
/// mid-restore can resume. Auth probes show only present/absent; secrets are never read.
/// </summary>
public sealed class InstallViewModel : ObservableObject
{
    private readonly IInstallManifestLoader _loader;
    private readonly InstallPlanner _planner;
    private readonly IAuthProbe _authProbe;
    private readonly IRestoreStateStore _stateStore;
    private readonly ISafetyGate _gate;
    private readonly IPlanExecutor _executor;
    private readonly InstallRunner _runner;

    private InstallManifest _manifest = InstallManifest.Empty;
    private OperationPlan? _plan;
    // The last built plan result, kept so the host-safe EXPORT step can project it without re-planning.
    private InstallPlanResult? _planResult;
    private string _approvedHash = string.Empty;
    // Maps a planned action id back to the manifest entry id, so the checkpoint can be updated per result.
    private readonly Dictionary<string, string> _actionToEntry = new();

    private bool _isBusy;
    private bool _isPreviewApproved;
    private bool _hasPlan;
    private bool _canResume;
    private string _stateDirectory = string.Empty;
    private string _summary = string.Empty;
    private string _resultSummary = string.Empty;

    public InstallViewModel(
        I18n i18n,
        IInstallManifestLoader loader,
        InstallPlanner planner,
        IAuthProbe authProbe,
        IRestoreStateStore stateStore,
        ISafetyGate gate,
        IPlanExecutor executor,
        InstallRunner runner)
    {
        I18n = i18n;
        _loader = loader;
        _planner = planner;
        _authProbe = authProbe;
        _stateStore = stateStore;
        _gate = gate;
        _executor = executor;
        _runner = runner;

        LoadManifestCommand = new RelayCommand(() => LoadManifest());
        BuildPlanCommand = new RelayCommand(() => BuildPlan(), () => _manifest.Entries.Count > 0 && !IsBusy);
        ApproveCommand = new RelayCommand(() => IsPreviewApproved = true, () => HasPlan && !IsPreviewApproved);
        CancelApprovalCommand = new RelayCommand(() => IsPreviewApproved = false, () => IsPreviewApproved);
        RunCommand = new RelayCommand(() => Run(), () => HasPlan && IsPreviewApproved && !IsBusy);
        ResumeCommand = new RelayCommand(() => BuildPlan(), () => CanResume && !IsBusy);
        ExportPlanCommand = new RelayCommand(() => ExportPlan(),
            () => _planResult is not null && !string.IsNullOrWhiteSpace(StateDirectory) && !IsBusy);
    }

    public I18n I18n { get; }

    /// <summary>Ordered restore actions (the dry-run preview).</summary>
    public ObservableCollection<PlanRow> PlanRows { get; } = new();

    /// <summary>Entries skipped (manual-after, url-manual, non-Net driver, already-done, gate-blocked).</summary>
    public ObservableCollection<PlanRow> SkippedRows { get; } = new();

    /// <summary>The manual-after checklist the user runs by hand.</summary>
    public ObservableCollection<InstallEntry> ManualChecklist { get; } = new();

    /// <summary>Sign-in presence rows (existence only — never the contents).</summary>
    public ObservableCollection<AuthRow> AuthRows { get; } = new();

    /// <summary>Per-action results after a run.</summary>
    public ObservableCollection<PlanRow> ExecutionResults { get; } = new();

    public ICommand LoadManifestCommand { get; }
    public ICommand BuildPlanCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand CancelApprovalCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand ResumeCommand { get; }

    /// <summary>Host-safe EXPORT: write the built plan as <c>install_plan.json</c> into the state directory.</summary>
    public ICommand ExportPlanCommand { get; }

    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public bool HasPlan { get => _hasPlan; private set => SetField(ref _hasPlan, value); }
    public bool CanResume { get => _canResume; private set => SetField(ref _canResume, value); }
    public string Summary { get => _summary; private set => SetField(ref _summary, value); }
    public string ResultSummary { get => _resultSummary; private set => SetField(ref _resultSummary, value); }

    public bool IsPreviewApproved
    {
        get => _isPreviewApproved;
        private set
        {
            if (SetField(ref _isPreviewApproved, value))
                OnPropertyChanged(nameof(IsAwaitingApproval));
        }
    }

    /// <summary>True when a plan exists but the user has not yet approved it (the confirm gate is showing).</summary>
    public bool IsAwaitingApproval => HasPlan && !IsPreviewApproved;

    /// <summary>
    /// The directory (outside the repo) where the checkpoint lives. The integration / settings layer sets
    /// this to the chosen payload root; defaults to the user profile so a probe-less first run still works.
    /// </summary>
    public string StateDirectory
    {
        get => _stateDirectory;
        set
        {
            if (SetField(ref _stateDirectory, value ?? string.Empty))
                RefreshResumeAvailability();
        }
    }

    /// <summary>Loads (or reloads) the bundled reinstall manifest from the app's <c>manifests</c> folder.</summary>
    public void LoadManifest()
    {
        IsBusy = true;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "manifests", "90-install.json");
            _manifest = File.Exists(path) ? _loader.Load(path) : InstallManifest.Empty;

            BuildAuthRows();
            Summary = I18n.Format("install.loaded.summary", _manifest.Entries.Count);
            ResetPlanState();
            RefreshResumeAvailability();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Builds the ordered restore plan, skipping entries already done in the checkpoint.</summary>
    public void BuildPlan()
    {
        IsBusy = true;
        try
        {
            RestoreState state = LoadState();
            var now = DateTime.UtcNow;
            InstallPlanResult result = _planner.BuildPlan(_manifest, state, now);

            _plan = result.Plan;
            _planResult = result;
            _approvedHash = string.Empty;
            IsPreviewApproved = false;
            _actionToEntry.Clear();
            ExecutionResults.Clear();
            ResultSummary = string.Empty;

            // Use the planner's authoritative action-id → entry-id stamping so the post-run checkpoint
            // marks the right entries done/failed (no positional re-derivation — L10).
            _actionToEntry.Clear();
            foreach (var kv in result.ActionEntryIds)
                _actionToEntry[kv.Key] = kv.Value;

            PlanRows.Clear();
            foreach (PlannedAction a in result.Plan.Actions)
                PlanRows.Add(PlanRow.FromAction(a));

            SkippedRows.Clear();
            foreach (InstallSkip s in result.Skipped)
                SkippedRows.Add(PlanRow.FromSkipped(SkipAsAction(s), s.Note));

            ManualChecklist.Clear();
            foreach (InstallEntry e in result.ManualChecklist)
                ManualChecklist.Add(e);

            HasPlan = !_plan.IsEmpty;
            Summary = I18n.Format("install.plan.summary",
                result.Plan.Actions.Count, result.ManualChecklist.Count, result.Skipped.Count);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Executes the approved plan through the gated executor and persists the checkpoint from the report.
    /// Guarded so nothing runs without an explicit approval; the approved hash is captured from the exact
    /// previewed plan (TOCTOU).
    /// </summary>
    public void Run()
    {
        if (_plan is null || !IsPreviewApproved || _plan.IsEmpty)
            return;

        IsBusy = true;
        try
        {
            _approvedHash = _plan.ComputeHash();
            PlanExecutionReport report = _executor.ExecuteWithReport(_plan, _approvedHash);

            ExecutionResults.Clear();
            foreach (PlanActionResult r in report.Results)
                ExecutionResults.Add(ResultRow(r));

            PersistCheckpoint(report);

            ResultSummary = I18n.Format("install.result.summary",
                report.DoneCount, report.SkippedOrNotRunCount, report.FailedCount);

            // After a run, approval is consumed; further runs (resume) re-plan from the checkpoint.
            IsPreviewApproved = false;
            RefreshResumeAvailability();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Host-safe EXPORT (Step 3 dry-run): project the most recently built plan into <c>install_plan.json</c> and
    /// write it into the state directory (outside the repo, frequently external/USB media). This reads the plan
    /// and writes JSON only — it NEVER runs winget/npm, spawns a process, or elevates; the writer re-gates the
    /// payload root first, so a protected/system target is refused. The destructive <see cref="Run"/> path is
    /// untouched.
    /// </summary>
    public void ExportPlan()
    {
        if (_planResult is null || string.IsNullOrWhiteSpace(StateDirectory))
            return;

        IsBusy = true;
        try
        {
            InstallRunResult export = _runner.ExportPlan(_planResult, StateDirectory, _gate);
            Summary = export.Authorized
                ? I18n.Format("install.export.summary", export.Export.Items.Count)
                : I18n["install.export.refused"];
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- helpers ----

    private RestoreState LoadState()
        => string.IsNullOrWhiteSpace(_stateDirectory) ? RestoreState.Empty : _stateStore.Load(_stateDirectory);

    private void PersistCheckpoint(PlanExecutionReport report)
    {
        if (string.IsNullOrWhiteSpace(_stateDirectory))
            return;

        RestoreState state = _stateStore.Load(_stateDirectory);
        if (string.IsNullOrEmpty(state.PlanHash))
            state = state with { PlanHash = report.PlanHash, StartedUtc = DateTime.UtcNow };

        var now = DateTime.UtcNow;
        foreach (PlanActionResult r in report.Results)
        {
            if (!_actionToEntry.TryGetValue(r.ActionId, out string? entryId) || entryId is null)
                continue;
            RestoreEntryStatus status = r.Status switch
            {
                PlanActionStatus.Done => RestoreEntryStatus.Done,
                PlanActionStatus.Failed or PlanActionStatus.Blocked => RestoreEntryStatus.Failed,
                _ => RestoreEntryStatus.Pending,
            };
            state = state.With(entryId, status, now);
        }

        _stateStore.Save(_stateDirectory, state);
        RefreshResumeAvailability();
    }

    private void RefreshResumeAvailability()
    {
        if (string.IsNullOrWhiteSpace(_stateDirectory))
        {
            CanResume = false;
            return;
        }
        RestoreState state = _stateStore.Load(_stateDirectory);
        CanResume = state.Entries.Count > 0 && state.FirstUnfinished() is not null;
    }

    private void BuildAuthRows()
    {
        AuthRows.Clear();
        foreach (InstallEntry e in _manifest.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.AuthProbe))
                continue;
            bool present = _authProbe.Exists(e.AuthProbe);
            string label = string.IsNullOrWhiteSpace(e.AuthKey) ? e.Id : e.AuthKey!;
            AuthRows.Add(new AuthRow
            {
                Label = label,
                Present = present,
                StatusText = present ? I18n["install.auth.present"] : I18n["install.auth.missing"],
            });
        }
    }

    private void ResetPlanState()
    {
        _plan = null;
        _planResult = null;
        _approvedHash = string.Empty;
        _actionToEntry.Clear();
        IsPreviewApproved = false;
        HasPlan = false;
        PlanRows.Clear();
        SkippedRows.Clear();
        ManualChecklist.Clear();
        ExecutionResults.Clear();
        ResultSummary = string.Empty;
    }

    private static PlannedAction SkipAsAction(InstallSkip skip)
    {
        // A lightweight, gate-irrelevant action purely to reuse PlanRow.FromSkipped for display.
        string id = skip.Entry.WingetId ?? skip.Entry.NpmPackage ?? skip.Entry.Id;
        return new CommandAction
        {
            FileName = id,
            Arguments = Array.Empty<string>(),
            Description = string.IsNullOrWhiteSpace(skip.Entry.Description) ? id : skip.Entry.Description,
            Reason = skip.Reason.ToString(),
        };
    }

    private PlanRow ResultRow(PlanActionResult r)
    {
        bool ok = r.Status == PlanActionStatus.Done;
        bool skipped = r.Status is PlanActionStatus.NotRun or PlanActionStatus.Skipped;
        return new PlanRow
        {
            Text = $"{r.Kind}: {r.Detail}",
            RiskText = r.Status.ToString().ToUpperInvariant(),
            RiskBrush = RiskVisuals.For(ok ? RiskLevel.Low : skipped ? RiskLevel.Info : RiskLevel.Critical),
            Undo = string.Empty,
            Detail = r.ActionId,
        };
    }
}

using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using WindowsCareKit.App.Deployment;
using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Module.Install.ViewModels;

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
    /// <summary>The shipped manifest folder and file, addressed relative to the app root (never to the
    /// process's ambient base directory, which a link-launched process reports as the link's folder).</summary>
    private const string ManifestsFolderName = "manifests";
    private const string InstallManifestFileName = "90-install.json";

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
    private string _checkpointWarning = string.Empty;
    private string _manifestInfoNote = string.Empty;
    private string _manifestHealthNote = string.Empty;

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
        RunCommand = new AsyncRelayCommand(RunAsync, () => HasPlan && IsPreviewApproved && !IsBusy);
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
    public string CheckpointWarning { get => _checkpointWarning; private set => SetField(ref _checkpointWarning, value); }
    public string ManifestInfoNote { get => _manifestInfoNote; private set => SetField(ref _manifestInfoNote, value); }
    public string ManifestHealthNote { get => _manifestHealthNote; private set => SetField(ref _manifestHealthNote, value); }

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
            if (IsBusy)
                return;

            if (SetField(ref _stateDirectory, value ?? string.Empty))
            {
                ResetPlanState();
                RefreshResumeAvailability();
            }
        }
    }

    /// <summary>Loads (or reloads) the bundled reinstall manifest from the app's <c>manifests</c> folder.</summary>
    public void LoadManifest()
    {
        IsBusy = true;
        try
        {
            string path = AppLayout.Current.Resource(Path.Combine(ManifestsFolderName, InstallManifestFileName));
            InstallManifestLoadResult load = _loader.Load(path);
            _manifest = load.Manifest;
            ManifestInfoNote = load.Status == InstallManifestLoadStatus.NotInstalled
                ? I18n["install.manifest.notInstalled"]
                : string.Empty;
            ManifestHealthNote = load.Status is InstallManifestLoadStatus.Malformed or InstallManifestLoadStatus.Unreadable
                ? I18n.Format("install.manifest.failed", load.ManifestPath,
                    $"{FailureCause(load.Status)}, {load.FailureCategory ?? load.Status.ToString()}")
                : string.Empty;

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

    /// <summary>Localized cause clause for a failed manifest load. "A corrupt file" and "a file we could not
    /// read" call for different user action (repair/reinstall vs. release the lock or fix permissions), and the
    /// raw <c>FailureCategory</c> that follows it is an untranslated CLR type name — a diagnostic token, not
    /// language. Retaining both keeps the sentence actionable in every locale without losing the token a bug
    /// report needs. Falls back to the status name so a future status is visibly unmapped rather than mislabelled.</summary>
    private string FailureCause(InstallManifestLoadStatus status) => status switch
    {
        InstallManifestLoadStatus.Malformed => I18n["install.manifest.cause.corrupt"],
        InstallManifestLoadStatus.Unreadable => I18n["install.manifest.cause.unreadable"],
        _ => status.ToString(),
    };

    /// <summary>Builds the ordered restore plan, skipping entries already done in the checkpoint.</summary>
    public void BuildPlan()
    {
        IsBusy = true;
        try
        {
            RestoreStateLoad load = LoadCheckpoint();
            if (!load.CanPlanResume)
            {
                ResetPlanState();
                CheckpointWarning = I18n["install.checkpoint.unreadable"];
                CanResume = false;
                return;
            }

            RestoreState state = load.State;
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
                PlanRows.Add(PlanRow.FromAction(a, I18n));

            SkippedRows.Clear();
            foreach (InstallSkip s in result.Skipped)
                SkippedRows.Add(PlanRow.FromSkipped(SkipAsAction(s), s.Note, I18n));

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
    public async Task RunAsync()
    {
        if (_plan is null || !IsPreviewApproved || _plan.IsEmpty)
            return;

        IsBusy = true;
        try
        {
            _approvedHash = _plan.ComputeHash();
            PlanExecutionReport report = await Task.Run(() => _executor.ExecuteWithReport(_plan, _approvedHash));

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
    /// payload root first, so a protected/system target is refused. The destructive <see cref="RunAsync"/> path is
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

    private RestoreStateLoad LoadCheckpoint()
        => string.IsNullOrWhiteSpace(_stateDirectory)
            ? RestoreStateLoad.Missing
            : _stateStore.TryLoad(_stateDirectory);

    private void PersistCheckpoint(PlanExecutionReport report)
    {
        if (string.IsNullOrWhiteSpace(_stateDirectory))
            return;

        RestoreStateLoad load = _stateStore.TryLoad(_stateDirectory);
        if (load.Status is RestoreStateLoadStatus.Corrupt or RestoreStateLoadStatus.Unavailable)
        {
            CheckpointWarning = I18n["install.checkpoint.notUpdated"];
            return;
        }

        RestoreState state = load.State;
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
        RestoreStateLoad load = _stateStore.TryLoad(_stateDirectory);
        CanResume = load.Status == RestoreStateLoadStatus.Loaded && load.State.FirstUnfinished() is not null;
        if (!load.CanPlanResume && string.IsNullOrEmpty(CheckpointWarning))
            CheckpointWarning = I18n["install.checkpoint.unreadable"];
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
        CheckpointWarning = string.Empty;
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

    /// <summary>One post-run outcome. Anything but Done means the install did not complete — a DISPOSITION, not
    /// an irreversible act — so the level stays Info and the row's own blockedness carries the colour.</summary>
    private PlanRow ResultRow(PlanActionResult r)
    {
        bool ok = r.Status == PlanActionStatus.Done;
        return new PlanRow
        {
            Text = $"{r.Kind}: {r.Detail}",
            RiskText = r.Status.ToString().ToUpperInvariant(),
            Risk = ok ? RiskLevel.Low : RiskLevel.Info,
            Undo = string.Empty,
            Detail = r.ActionId,
            Disposition = ok ? RowDisposition.Unstated : RowDisposition.WillNotRun,
        };
    }
}

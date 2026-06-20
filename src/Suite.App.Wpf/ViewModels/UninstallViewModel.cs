using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;

namespace WindowsCareKit.App.ViewModels;

/// <summary>
/// The Sil (Uninstall) view-model: lists installed programs and per-user Store apps, previews the
/// official-uninstaller plan and the leftover dry-run, and — after an explicit confirm — runs them
/// through <see cref="IExecutor"/> (the gated executor). Per-user AppX removal is a separate gated call
/// via <see cref="IAppxRemover"/>. Every destructive path is build-plan → preview → approve → execute;
/// there is no other execution path (spec §1.1, §3).
/// </summary>
public sealed class UninstallViewModel : ObservableObject
{
    private readonly IInstalledAppReader _appReader;
    private readonly IAppxReader _appxReader;
    private readonly ISafetyGate _gate;
    private readonly ILeftoverProbe _probe;
    private readonly IExecutor _executor;
    private readonly IAppxRemover _appxRemover;
    private readonly IFolderOpener _folderOpener;

    // The single source of truth: every desktop program + Store app as a flat row list. The ICollectionView
    // filters a VIEW over this — the backing list is never mutated by search (UI decision §2). A plain List
    // (not ObservableCollection) is deliberate: the view is refreshed explicitly via AppsView.Refresh(), so the
    // ListCollectionView never subscribes to a cross-thread CollectionChanged (load runs work off the UI thread).
    private readonly List<AppRow> _allRows = new();
    private string _search = string.Empty;
    private int _scopeIndex; // 0=All, 1=Desktop, 2=Store
    private bool _isLoading;
    private bool _isBusy;
    private AppRow? _selectedRow;
    private InstalledApp? _selectedApp;
    private InstalledAppx? _selectedAppx;
    private int _appxCount;

    // The leftover plan the user is currently previewing (reused verbatim when staging — H8).
    private OperationPlan? _previewedLeftoverPlan;

    // The plan currently staged for execution, the exact hash the user is about to approve, and what kind.
    private OperationPlan? _pendingPlan;
    private string? _pendingPlanHash;
    private PendingKind _pendingKind;
    private bool _hasResult;
    private string _resultSummary = string.Empty;

    /// <summary>Which run path is staged for confirmation.</summary>
    private enum PendingKind { None, Official, Leftovers, Appx }

    public UninstallViewModel(I18n i18n, IInstalledAppReader appReader, IAppxReader appxReader,
        ISafetyGate gate, ILeftoverProbe probe, IExecutor executor, IAppxRemover appxRemover,
        IFolderOpener folderOpener)
    {
        I18n = i18n;
        _appReader = appReader;
        _appxReader = appxReader;
        _gate = gate;
        _probe = probe;
        _executor = executor;
        _appxRemover = appxRemover;
        _folderOpener = folderOpener;

        // One ICollectionView over the flat row list. Search updates the Filter predicate and calls Refresh();
        // it NEVER clears/refills the source, so a staged plan/selection survives typing (UI decision §2 + test).
        AppsView = new ListCollectionView(_allRows) { Filter = MatchesSearch };

        RefreshCommand = new RelayCommand(async () => await LoadAsync());

        // Each "run" command stages a plan + asks for confirmation; it does NOT execute yet.
        RunOfficialCommand = new RelayCommand(StageOfficial, () => OfficialActions.Count > 0 && !IsBusy);
        RunLeftoverCommand = new RelayCommand(StageLeftovers, () => LeftoverActions.Count > 0 && !IsBusy);
        RemoveAppxCommand = new RelayCommand(StageAppx, () => _selectedAppx is not null && !IsBusy);

        // The detail pane's single primary action: stage whatever the selected row supports — the official
        // uninstaller for a desktop app, or Store removal for an AppX row. PR-4 swaps this for the wizard.
        UninstallSelectedCommand = new RelayCommand(StageSelected, () => CanUninstallSelected && !IsBusy);

        // "Yükleme dizinini aç" — open the selected app's install folder in Explorer (read-only, host-safe).
        OpenLocationCommand = new RelayCommand(OpenSelectedLocation, () => HasDetailLocation);

        // Confirm dialog buttons. These remain the canonical approve/cancel surface; the reusable
        // ConfirmGate (UI decision §B2) drives them through its own buttons via the Gate view-model below.
        ApproveCommand = new RelayCommand(async () => await ApproveAsync(), () => RequiresConfirmation && !IsBusy);
        CancelCommand = new RelayCommand(CancelPending, () => RequiresConfirmation && !IsBusy);

        // The reusable confirmation gate. It owns tier selection + type-to-confirm; Approve/Cancel delegate
        // straight back into the existing flow so the staging/hash semantics are untouched.
        Gate = new ConfirmGateViewModel(
            i18n,
            onApprove: () => ApproveCommand.Execute(null),
            onCancel: () => CancelCommand.Execute(null),
            isBusy: () => IsBusy);
    }

    public I18n I18n { get; }

    /// <summary>The reusable 3-tier confirmation gate (UI decision §B2) — the reference integration.</summary>
    public ConfirmGateViewModel Gate { get; }

    /// <summary>
    /// The filtered, name-sorted view the DataGrid binds to. Backed by <see cref="_allRows"/>; the search box
    /// only refreshes this view's filter, it never touches the backing list (UI decision §2 / non-mutation test).
    /// </summary>
    public ICollectionView AppsView { get; }

    public ObservableCollection<PlanRow> OfficialActions { get; } = new();
    public ObservableCollection<PlanRow> LeftoverActions { get; } = new();
    public ObservableCollection<PlanRow> SkippedActions { get; } = new();

    /// <summary>The backing row list — exposed read-only so tests can assert the filter never mutates it.</summary>
    public IReadOnlyList<AppRow> AllRows => _allRows;

    /// <summary>The per-action outcome rows from the most recent execution.</summary>
    public ObservableCollection<PlanRow> ExecutionResults { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand RunOfficialCommand { get; }
    public ICommand RunLeftoverCommand { get; }
    public ICommand RemoveAppxCommand { get; }
    public ICommand UninstallSelectedCommand { get; }
    public ICommand OpenLocationCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>The command-bar scope filter: 0 = All, 1 = Desktop only, 2 = Store only (UI decision §2: a
    /// trivial scope filter is allowed; Desktop/Store pills are deferred).</summary>
    public int ScopeIndex
    {
        get => _scopeIndex;
        set { if (SetField(ref _scopeIndex, value)) AppsView.Refresh(); }
    }

    public bool IsLoading { get => _isLoading; private set => SetField(ref _isLoading, value); }

    /// <summary>True while a plan is executing (or AppX removal is in flight) — disables the run buttons.</summary>
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }

    public int AppxCount { get => _appxCount; private set => SetField(ref _appxCount, value); }

    /// <summary>True when a plan is staged and the confirm panel should be shown.</summary>
    public bool RequiresConfirmation => _pendingKind != PendingKind.None;

    /// <summary>True once a plan has been approved-and-run at least once (drives the result panel).</summary>
    public bool HasResult { get => _hasResult; private set => SetField(ref _hasResult, value); }

    /// <summary>The "{0} done · {1} skipped · {2} failed" line for the last run.</summary>
    public string ResultSummary { get => _resultSummary; private set => SetField(ref _resultSummary, value); }

    public string Search
    {
        get => _search;
        // Refresh the VIEW only — the backing _allRows is untouched, so a staged plan/selection survives typing.
        set { if (SetField(ref _search, value)) AppsView.Refresh(); }
    }

    /// <summary>
    /// The single row selected in the unified grid. Setting it routes to the desktop-app or Store-app preview
    /// path (keeping the old <see cref="SelectedApp"/>/<see cref="SelectedAppx"/> semantics intact) and refreshes
    /// the lean detail pane (UI decision §3).
    /// </summary>
    public AppRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetField(ref _selectedRow, value))
                return;

            // Map the row back to the typed source selections the existing flow already understands.
            SelectedApp = value?.App;
            SelectedAppx = value?.Appx;

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanUninstallSelected));
            OnPropertyChanged(nameof(DetailTitle));
            OnPropertyChanged(nameof(DetailPublisher));
            OnPropertyChanged(nameof(DetailVersion));
            OnPropertyChanged(nameof(DetailLocation));
            OnPropertyChanged(nameof(HasDetailLocation));
            RaiseRunCommandStates();
        }
    }

    public InstalledApp? SelectedApp
    {
        get => _selectedApp;
        private set
        {
            if (SetField(ref _selectedApp, value))
            {
                CancelPending(); // a new selection invalidates any staged plan
                _ = BuildPreviewAsync(value);
            }
        }
    }

    public InstalledAppx? SelectedAppx
    {
        get => _selectedAppx;
        private set
        {
            if (SetField(ref _selectedAppx, value))
                OnPropertyChanged(nameof(HasAppxSelection));
        }
    }

    public bool HasSelection => _selectedRow is not null;
    public bool HasAppxSelection => _selectedAppx is not null;

    /// <summary>True when the selected row has something to uninstall: a desktop uninstaller, or a Store app.</summary>
    public bool CanUninstallSelected =>
        (_selectedApp is not null && OfficialActions.Count > 0) || _selectedAppx is not null;

    // ---- Lean, info-only detail-pane projections (UI decision §3 — identity, no plan dump). ----

    public string DetailTitle => _selectedRow?.DisplayName ?? string.Empty;
    public string DetailPublisher => _selectedRow?.Publisher ?? InstalledApp.Em;
    public string DetailVersion => _selectedRow?.Version ?? InstalledApp.Em;

    /// <summary>The install location for the detail pane ("Yükleme dizinini aç" target), or empty.</summary>
    public string DetailLocation =>
        _selectedApp?.InstallLocation ?? _selectedAppx?.InstallLocation ?? string.Empty;

    public bool HasDetailLocation => !string.IsNullOrWhiteSpace(DetailLocation);

    public async Task LoadAsync()
    {
        IsLoading = true;
        SelectedRow = null;
        _allRows.Clear();
        try
        {
            // Build a single flat, name-sorted list of desktop + Store rows (UI decision §2: one grid, no
            // grouping). The reads + the AppRow projection happen off the UI thread.
            var rows = await Task.Run(() =>
            {
                var apps = _appReader.ReadAll()
                    .Where(a => !a.IsSystemComponent)
                    .Select(AppRow.FromApp);
                var packages = _appxReader.ReadCurrentUserPackages()
                    .Where(p => !p.IsFrameworkOrSystem)
                    .Select(AppRow.FromAppx);
                return apps.Concat(packages)
                    .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            });

            AppxCount = rows.Count(r => r.IsStore);
            foreach (var r in rows)
            {
                r.BadgeText = LocalizeBadge(r.StatusBadge);
                _allRows.Add(r);
            }
            AppsView.Refresh();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Maps an <see cref="AppRow"/> status token to its localized "[…]" label (empty token → empty).</summary>
    private string LocalizeBadge(string token) => token switch
    {
        AppRow.StoreBadge => I18n["uninstall.badge.store"],
        AppRow.AdminBadge => I18n["uninstall.badge.admin"],
        AppRow.BrokenBadge => I18n["uninstall.badge.broken"],
        _ => string.Empty,
    };

    /// <summary>The ICollectionView <c>Filter</c> predicate — pure, never mutates the source (UI decision §2).</summary>
    private bool MatchesSearch(object item)
    {
        if (item is not AppRow row)
            return false;

        // Scope: 1 = Desktop only, 2 = Store only, 0 = All.
        if (_scopeIndex == 1 && row.IsStore)
            return false;
        if (_scopeIndex == 2 && !row.IsStore)
            return false;

        if (string.IsNullOrWhiteSpace(_search))
            return true;
        return row.SearchKey.Contains(_search.Trim().ToLowerInvariant());
    }

    private void OpenSelectedLocation()
    {
        // Benign read-only UI affordance — goes through the sanctioned IFolderOpener (validates the directory,
        // pins Explorer to its System path), NEVER a direct Process.Start (banned API / spec §3).
        string path = DetailLocation;
        if (!string.IsNullOrWhiteSpace(path))
            _folderOpener.OpenFolder(path);
    }

    private async Task BuildPreviewAsync(InstalledApp? app)
    {
        OfficialActions.Clear();
        LeftoverActions.Clear();
        SkippedActions.Clear();
        _previewedLeftoverPlan = null;
        // OfficialActions just changed (cleared) — keep the detail-pane "Kaldır →" enablement in step.
        OnPropertyChanged(nameof(CanUninstallSelected));
        RaiseRunCommandStates();
        if (app is null)
            return;

        var now = DateTime.UtcNow;

        // Official uninstaller plan — pure, cheap.
        var official = OfficialUninstallerPlanner.Build(app, now);
        if (official is not null)
            foreach (var a in official.Actions)
                OfficialActions.Add(PlanRow.FromAction(a));

        // OfficialActions now reflects this app — refresh the detail-pane primary-action enablement.
        OnPropertyChanged(nameof(CanUninstallSelected));
        RaiseRunCommandStates();

        // Leftover scan touches the filesystem/registry — run it off the UI thread.
        var scanner = new LeftoverScanner(_probe, _gate);
        LeftoverScanResult result = await Task.Run(() => scanner.Scan(app, now));

        // Selection may have changed while scanning; only apply if still current.
        if (!ReferenceEquals(app, _selectedApp))
            return;

        // Keep the EXACT plan object the rows were rendered from, so staging runs precisely what the user
        // previewed — never a second scan whose results could differ (H8 dry-run honesty).
        _previewedLeftoverPlan = result.Plan;

        foreach (var a in result.Plan.Actions)
            LeftoverActions.Add(PlanRow.FromAction(a));
        foreach (var s in result.Skipped)
            SkippedActions.Add(PlanRow.FromSkipped(s.Action, s.Reason));
    }

    // ---- Stage (build plan + ask to confirm). Nothing executes here. ----

    /// <summary>
    /// The detail-pane "Kaldır →" handler: stage whatever the selected row supports. For PR-2 this reuses the
    /// EXISTING official-uninstaller / Store-removal staging (PR-4 will replace it with the multi-beat wizard).
    /// </summary>
    private void StageSelected()
    {
        if (_selectedAppx is not null)
        {
            StageAppx();
            return;
        }
        if (_selectedApp is not null && OfficialActions.Count > 0)
            StageOfficial();
    }

    private void StageOfficial()
    {
        var app = _selectedApp;
        if (app is null)
            return;

        OperationPlan? plan = OfficialUninstallerPlanner.Build(app, DateTime.UtcNow);
        if (plan is null || plan.IsEmpty)
            return;

        Stage(plan, PendingKind.Official);
    }

    private void StageLeftovers()
    {
        // Stage the EXACT plan the user previewed — never a fresh scan (which could differ from the rows
        // on screen). The hash is therefore the preview-time hash (H8 / spec §2).
        OperationPlan? plan = _previewedLeftoverPlan;
        if (plan is null || plan.IsEmpty)
            return;

        Stage(plan, PendingKind.Leftovers);
    }

    private void StageAppx()
    {
        InstalledAppx? package = _selectedAppx;
        if (package is null)
            return;

        // AppX removal is not a typed plan; we still route it through the same confirm gate. Store app
        // removal can't be undone, so it is always the IRREVERSIBLE tier (type-to-confirm).
        _pendingPlan = null;
        _pendingPlanHash = null;
        _pendingKind = PendingKind.Appx;

        var rows = new[]
        {
            ResultRow(I18n.Format("uninstall.confirm.appx.row", package.DisplayName), "❌",
                RiskLevel.Critical, package.PackageFullName),
        };
        Gate.Open(ConfirmTier.Irreversible, I18n["uninstall.confirm.title"],
            I18n["uninstall.appx.irreversible"], rows);
        RaiseConfirmationState();
    }

    private void Stage(OperationPlan plan, PendingKind kind)
    {
        _pendingPlan = plan;
        _pendingPlanHash = plan.ComputeHash(); // captured from the EXACT previewed/staged plan (spec §3)
        _pendingKind = kind;

        // Open the reusable gate with the tier chosen from the plan's irreversibility, the honest body, and
        // the EXACT dry-run rows the user is about to approve (UI decision §B2).
        ConfirmTier tier = ConfirmGateViewModel.TierFor(plan);
        var rows = plan.Actions.Select(PlanRow.FromAction);
        Gate.Open(tier, I18n["uninstall.confirm.title"], I18n["uninstall.confirm.body"], rows);
        RaiseConfirmationState();
    }

    private void CancelPending()
    {
        if (_pendingKind == PendingKind.None)
            return;
        _pendingPlan = null;
        _pendingPlanHash = null;
        _pendingKind = PendingKind.None;
        Gate.Close();
        RaiseConfirmationState();
    }

    // ---- Approve (the ONLY place that calls the executor / appx remover). ----

    private async Task ApproveAsync()
    {
        if (_pendingKind == PendingKind.None)
            return;

        PendingKind kind = _pendingKind;
        OperationPlan? plan = _pendingPlan;
        string? hash = _pendingPlanHash;

        // Approval is captured into locals above, so the confirm panel can be dismissed BEFORE we run —
        // the user has approved, and clearing now avoids any race where the result lands before the
        // confirm state is reset.
        _pendingPlan = null;
        _pendingPlanHash = null;
        _pendingKind = PendingKind.None;
        IsBusy = true;
        Gate.Close();
        RaiseConfirmationState();
        try
        {
            if (kind == PendingKind.Appx)
            {
                await RunAppxRemovalAsync();
            }
            else if (plan is not null && hash is not null)
            {
                await RunPlanAsync(plan, hash);
            }
        }
        finally
        {
            IsBusy = false;
            RaiseConfirmationState();
        }
    }

    private async Task RunPlanAsync(OperationPlan plan, string approvedHash)
    {
        ExecutionResults.Clear();

        ExecutionReport report = await Task.Run(() =>
        {
            // The executor is the GatedExecutor; ExecuteWithReport gives the per-action breakdown.
            return _executor is GatedExecutor gated
                ? gated.ExecuteWithReport(plan, approvedHash)
                : ToReport(_executor.Execute(plan, approvedHash), plan);
        });

        RenderReport(report, plan);
    }

    private async Task RunAppxRemovalAsync()
    {
        InstalledAppx? package = _selectedAppx;
        if (package is null)
            return;

        ExecutionResults.Clear();

        AppxRemovalResult result = await _appxRemover.RemoveCurrentUserAsync(package);

        ExecutionResults.Add(result.Removed
            ? ResultRow($"Removed Store app: {package.DisplayName}", "Done", RiskLevel.Low, result.Reason)
            : ResultRow($"Store app not removed: {package.DisplayName}", "Failed", RiskLevel.Critical, result.Reason));

        int done = result.Removed ? 1 : 0;
        int failed = result.Removed ? 0 : 1;
        ResultSummary = I18n.Format("uninstall.result.summary", done, 0, failed);
        HasResult = true;

        if (result.Removed)
        {
            // Drop the removed Store app's row from the single backing list, then refresh the view off it.
            AppRow? row = _allRows.FirstOrDefault(r => ReferenceEquals(r.Appx, package));
            if (row is not null)
                _allRows.Remove(row);
            AppxCount = _allRows.Count(r => r.IsStore);
            SelectedRow = null;
            AppsView.Refresh();
        }
    }

    private void RenderReport(ExecutionReport report, OperationPlan plan)
    {
        // Map each action result back to a readable row (action descriptions live on the plan).
        var byId = plan.Actions.ToDictionary(a => a.Id, a => a.Description);
        int skipped = 0;
        foreach (var r in report.Results)
        {
            string text = byId.TryGetValue(r.ActionId, out var desc) ? desc : r.Kind;
            RiskLevel risk = r.Status switch
            {
                ActionStatus.Done => RiskLevel.Low,
                ActionStatus.NotRun => RiskLevel.Info,
                _ => RiskLevel.Critical,
            };
            if (r.Status == ActionStatus.NotRun)
                skipped++;
            ExecutionResults.Add(ResultRow(text, r.Status.ToString(), risk, r.Detail));
        }

        ResultSummary = I18n.Format("uninstall.result.summary", report.DoneCount, skipped, report.FailedCount);
        HasResult = true;

        // If the official uninstaller / leftovers ran, the world changed — refresh the leftover preview.
        if (report.Authorized && _selectedApp is not null)
            _ = BuildPreviewAsync(_selectedApp);
    }

    private static ExecutionReport ToReport(ExecutionOutcome outcome, OperationPlan plan)
    {
        // Fallback for a non-GatedExecutor IExecutor (e.g. a test double): synthesize a coarse report.
        ActionStatus status = outcome.Ran ? ActionStatus.Done : ActionStatus.NotRun;
        var results = plan.Actions
            .Select(a => new ActionResult(a.Id, a.Kind, status, outcome.Reason))
            .ToArray();
        return new ExecutionReport(outcome.Ran, plan.ComputeHash(), results);
    }

    private void RaiseConfirmationState()
    {
        OnPropertyChanged(nameof(RequiresConfirmation));
        OnPropertyChanged(nameof(IsBusy));
        Gate.RefreshBusy(); // keep the gate's Approve/Cancel enablement in step with IsBusy
    }

    /// <summary>Re-queries the run/uninstall commands' CanExecute after a selection or preview change.</summary>
    private static void RaiseRunCommandStates() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    /// <summary>Builds a result row reusing the same <see cref="PlanRow"/> shape + Strongbox risk palette.</summary>
    private static PlanRow ResultRow(string text, string statusText, RiskLevel risk, string detail) => new()
    {
        Text = text,
        RiskText = statusText,
        RiskBrush = RiskVisuals.For(risk),
        Undo = string.Empty,
        Detail = detail,
    };
}

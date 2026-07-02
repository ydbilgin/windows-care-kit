using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.App.ViewModels;

/// <summary>
/// The Sil (Uninstall) view-model: lists installed programs and per-user Store apps. A desktop app's
/// official-uninstaller + leftover removal is owned ENTIRELY by the 4-beat <see cref="UninstallWizardViewModel"/>
/// (opened from the detail-pane "Kaldır →"); this VM stages only the per-user AppX removal — a single-shot,
/// gated call via <see cref="IAppxRemover"/> behind an explicit type-to-confirm. Store removal is the one
/// destructive path this VM owns: stage → confirm → remove (spec §1.1, §3).
/// </summary>
public sealed class UninstallViewModel : ObservableObject
{
    private readonly IInstalledAppReader _appReader;
    private readonly IAppxReader _appxReader;
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

    // Which run path is staged for confirmation (only the Store-app removal lives here).
    private PendingKind _pendingKind;
    private InstalledAppx? _pendingAppx;
    private bool _hasResult;
    private string _resultSummary = string.Empty;

    /// <summary>Which run path is staged for confirmation. The desktop official path lives in the wizard;
    /// this VM stages only the Store-app removal.</summary>
    private enum PendingKind { None, Appx }

    public UninstallViewModel(I18n i18n, IInstalledAppReader appReader, IAppxReader appxReader,
        ISafetyGate gate, ILeftoverProbe probe, IExecutor executor, IAppxRemover appxRemover,
        IFolderOpener folderOpener, IRestorePointCapabilityProbe? restorePointCapability = null)
    {
        I18n = i18n;
        _appReader = appReader;
        _appxReader = appxReader;
        _appxRemover = appxRemover;
        _folderOpener = folderOpener;
        // gate + probe are forwarded to the wizard (the single leftover-deletion path); this VM no longer
        // runs its own leftover scan.

        // One ICollectionView over the flat row list. Search updates the Filter predicate and calls Refresh();
        // it NEVER clears/refills the source, so a staged plan/selection survives typing (UI decision §2 + test).
        AppsView = new ListCollectionView(_allRows) { Filter = MatchesSearch };

        RefreshCommand = new RelayCommand(async () => await LoadAsync());

        // The official uninstaller for a desktop app is driven solely by the wizard (the single official+leftover
        // path); this VM only stages the Store-app removal. Staging asks for confirmation — it does NOT execute yet.
        RemoveAppxCommand = new RelayCommand(StageAppx, () => _selectedAppx is not null && !IsBusy);

        // The detail pane's single primary action: a desktop app opens the wizard (official + leftovers); an
        // AppX row stages the single-shot Store removal. See StageSelected.
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

        // The 4-beat uninstall wizard (PR-4). It owns its OWN confirm gate (ConfirmGate #1 official + #2
        // leftovers reuse) and runs plans through the SAME executor — the detail-pane "Kaldır →" opens it for a
        // desktop app. A Store app keeps the existing single-shot irreversible removal (no wizard / no leftover
        // scan — a Store app has neither an official-uninstaller plan nor registry leftovers, UI decision §4).
        Wizard = new UninstallWizardViewModel(i18n, gate, probe, executor, utcNow: null,
            restorePointCapability: restorePointCapability);
    }

    public I18n I18n { get; }

    /// <summary>The reusable 3-tier confirmation gate (UI decision §B2) — the reference integration.</summary>
    public ConfirmGateViewModel Gate { get; }

    /// <summary>The 4-beat uninstall wizard overlay (PR-4), opened by the detail-pane "Kaldır →" for desktop apps.</summary>
    public UninstallWizardViewModel Wizard { get; }

    /// <summary>
    /// The filtered, name-sorted view the DataGrid binds to. Backed by <see cref="_allRows"/>; the search box
    /// only refreshes this view's filter, it never touches the backing list (UI decision §2 / non-mutation test).
    /// </summary>
    public ICollectionView AppsView { get; }

    /// <summary>The backing row list — exposed read-only so tests can assert the filter never mutates it.</summary>
    public IReadOnlyList<AppRow> AllRows => _allRows;

    /// <summary>The per-action outcome rows from the most recent execution.</summary>
    public ObservableCollection<PlanRow> ExecutionResults { get; } = new();

    public ICommand RefreshCommand { get; }
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
                CancelPending(); // a new selection invalidates any staged plan
        }
    }

    public InstalledAppx? SelectedAppx
    {
        get => _selectedAppx;
        private set
        {
            if (SetField(ref _selectedAppx, value))
            {
                CancelPending(); // a new Store-app selection invalidates any staged removal
                OnPropertyChanged(nameof(HasAppxSelection));
            }
        }
    }

    public bool HasSelection => _selectedRow is not null;
    public bool HasAppxSelection => _selectedAppx is not null;

    /// <summary>
    /// True when the selected row can be removed: ANY desktop app (the wizard handles even a broken-uninstaller
    /// app — it can still scan + remove leftovers), or a Store app (single-shot removal).
    /// </summary>
    public bool CanUninstallSelected => _selectedApp is not null || _selectedAppx is not null;

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

    // ---- Stage (build plan + ask to confirm). Nothing executes here. ----

    /// <summary>
    /// The detail-pane "Kaldır →" handler. A desktop app opens the 4-beat wizard (PR-4); a Store app keeps the
    /// EXISTING single-shot irreversible removal (a Store app has no official-uninstaller plan and no registry
    /// leftovers, so the multi-beat wizard would be empty — UI decision §4).
    /// </summary>
    private void StageSelected()
    {
        if (_selectedAppx is not null)
        {
            StageAppx();
            return;
        }
        if (_selectedApp is not null)
            Wizard.Open(_selectedApp);
    }

    private void StageAppx()
    {
        InstalledAppx? package = _selectedAppx;
        if (package is null)
            return;

        // AppX removal is not a typed plan; we still route it through the same confirm gate. Store app
        // removal can't be undone, so it is always the IRREVERSIBLE tier (type-to-confirm).
        _pendingKind = PendingKind.Appx;
        _pendingAppx = package;

        var rows = new[]
        {
            ResultRow(I18n.Format("uninstall.confirm.appx.row", package.DisplayName), "❌",
                RiskLevel.Critical, package.PackageFullName),
        };
        Gate.Open(ConfirmTier.Irreversible, I18n["uninstall.confirm.title"],
            I18n["uninstall.appx.irreversible"], rows);
        RaiseConfirmationState();
    }

    private void CancelPending()
    {
        if (_pendingKind == PendingKind.None)
            return;
        _pendingKind = PendingKind.None;
        _pendingAppx = null;
        Gate.Close();
        RaiseConfirmationState();
    }

    // ---- Approve (the ONLY place that calls the appx remover). ----

    private async Task ApproveAsync()
    {
        // The desktop official path lives in the wizard; the only thing this VM stages + approves is the
        // single-shot Store-app removal (UI decision §4).
        if (_pendingKind != PendingKind.Appx)
            return;
        InstalledAppx? package = _pendingAppx;
        if (package is null)
        {
            CancelPending();
            return;
        }

        // The user has approved — clear the staged state and dismiss the confirm panel BEFORE we run, so the
        // result can never land before the confirm state is reset.
        _pendingKind = PendingKind.None;
        _pendingAppx = null;
        IsBusy = true;
        Gate.Close();
        RaiseConfirmationState();
        try
        {
            await RunAppxRemovalAsync(package);
        }
        finally
        {
            IsBusy = false;
            RaiseConfirmationState();
        }
    }

    private async Task RunAppxRemovalAsync(InstalledAppx package)
    {
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

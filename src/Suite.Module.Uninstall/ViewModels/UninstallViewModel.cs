using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using WindowsCareKit.App.Controls;
using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Module.Uninstall.ViewModels;

/// <summary>
/// The Uninstall view-model: an inventory of installed programs and per-user Store apps, plus ONE destructive
/// door per selected app.
/// <para>
/// For a desktop program that door composes a single plan — an optional protective restore point, the
/// vendor's own uninstaller, and the program-owned leftovers — and shows all of it in ONE
/// <see cref="ConfirmGateViewModel"/>. The user vetoes leftovers row by row inside the gate;
/// <see cref="PlanSelection.Subset"/> then composes exactly what they approved, and that subset is what is
/// hashed and what is executed (SPEC §1.1, §3 M3). This replaces the 4-beat wizard's two separate approval
/// doors: the vendor step and the leftovers are now decided together, before either runs.
/// </para>
/// <para>
/// A Store app keeps its existing single-shot removal: it has no vendor uninstaller plan and no registry
/// leftovers, so there is nothing to compose a subset out of.
/// </para>
/// </summary>
public sealed class UninstallViewModel : ObservableObject, IWckStartupAware
{
    private readonly IInstalledAppReader _appReader;
    private readonly IAppxReader _appxReader;
    private readonly ISafetyGate _gate;
    private readonly ILeftoverProbe _probe;
    private readonly IPlanExecutor _executor;
    private readonly IFolderOpener _folderOpener;
    private readonly IRestorePointCapabilityProbe? _restorePointCapability;
    private readonly RemovalPlanComposer _composer;

    // The single source of truth: every desktop program + Store app as a flat row list. The ICollectionView
    // filters a VIEW over this — the backing list is never mutated by search (UI decision §2). A plain List
    // (not ObservableCollection) is deliberate: the view is refreshed explicitly via AppsView.Refresh(), so the
    // ListCollectionView never subscribes to a cross-thread CollectionChanged (load runs work off the UI thread).
    private readonly List<AppRow> _allRows = new();
    private string _search = string.Empty;
    private int _scopeIndex; // 0=All, 1=Desktop, 2=Store
    private int _loadGeneration;
    private bool _isLoading;
    private bool _isBusy;
    private bool _technicalDetails = true;
    private AppRow? _selectedRow;
    private InstalledApp? _selectedApp;
    private InstalledAppx? _selectedAppx;
    private int _appxCount;
    private string _inventoryNotice = string.Empty;

    // The selected app's removal preview: the scan behind it, the ONE composed staging the rail previews and
    // the gate approves, and the honest failure note.
    private LeftoverScanResult? _scan;
    private RemovalStaging? _staging;
    private bool _isScanning;
    private bool _restorePointEnabled;
    private string _buildError = string.Empty;
    private string _protectedNote = string.Empty;

    // Which run path is staged for confirmation, and what it would execute.
    private PendingKind _pendingKind;
    private InstalledAppx? _pendingAppx;
    private RemovalStaging? _pendingRemoval;
    private bool _hasResult;
    private string _resultSummary = string.Empty;

    /// <summary>Which run path is staged for confirmation: a desktop program's composed removal, or the
    /// single-shot Store-app removal.</summary>
    private enum PendingKind { None, Removal, Appx }

    public UninstallViewModel(I18n i18n, IInstalledAppReader appReader, IAppxReader appxReader,
        ISafetyGate gate, ILeftoverProbe probe, IPlanExecutor executor,
        IFolderOpener folderOpener, IRestorePointCapabilityProbe? restorePointCapability = null)
    {
        I18n = i18n;
        _appReader = appReader;
        _appxReader = appxReader;
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _executor = executor;
        _folderOpener = folderOpener;
        _restorePointCapability = restorePointCapability;
        _composer = new RemovalPlanComposer(i18n);

        // One ICollectionView over the flat row list. Search updates the Filter predicate and calls Refresh();
        // it NEVER clears/refills the source, so a staged plan/selection survives typing (UI decision §2 + test).
        AppsView = new ListCollectionView(_allRows) { Filter = MatchesSearch };

        RefreshCommand = new AsyncRelayCommand(_ => LoadAsync());

        // ONE scan action, not three self-describing-identical depth modes (A-M3-1, decision §2.5-10).
        ScanLeftoversCommand = new AsyncRelayCommand(ScanLeftoversAsync, () => CanScanLeftovers);

        // Staging asks for confirmation — it does NOT execute.
        RemoveAppxCommand = new RelayCommand(StageAppx, () => _selectedAppx is not null && !IsBusy);
        UninstallSelectedCommand = new RelayCommand(StageSelected, () => CanUninstallSelected && !IsBusy);

        // "Open install folder" — read-only, host-safe.
        OpenLocationCommand = new RelayCommand(OpenSelectedLocation, () => HasDetailLocation);

        // Confirm dialog buttons. These remain the canonical approve/cancel surface; the reusable
        // ConfirmGate (UI decision §B2) drives them through its own buttons via the Gate view-model below.
        ApproveCommand = new AsyncRelayCommand(ApproveAsync, () => RequiresConfirmation && !IsBusy);
        CancelCommand = new RelayCommand(CancelPending, () => RequiresConfirmation && !IsBusy);

        // The one approval door. It owns tier selection + type-to-confirm; Approve/Cancel delegate straight
        // back into this flow, so the staging/hash semantics have a single implementation.
        Gate = new ConfirmGateViewModel(
            i18n,
            onApprove: ApproveAsync,
            onCancel: () => CancelCommand.Execute(null),
            isBusy: () => IsBusy);

        I18n.PropertyChanged += OnLocalizationChanged;
    }

    public I18n I18n { get; }

    /// <summary>The reusable 3-tier confirmation gate (UI decision §B2) — this screen's only approval door.</summary>
    public ConfirmGateViewModel Gate { get; }

    /// <summary>
    /// The filtered, name-sorted view the DataGrid binds to. Backed by <see cref="_allRows"/>; the search box
    /// only refreshes this view's filter, it never touches the backing list (UI decision §2 / non-mutation test).
    /// </summary>
    public ICollectionView AppsView { get; }

    /// <summary>The backing row list — exposed read-only so tests can assert the filter never mutates it.</summary>
    public IReadOnlyList<AppRow> AllRows => _allRows;

    /// <summary>The per-action outcome rows from the most recent execution.</summary>
    public ObservableCollection<PlanRow> ExecutionResults { get; } = new();

    /// <summary>The selected app's program-owned leftovers — vetoable rows, all starting unchecked.</summary>
    public ObservableCollection<PlanRow> LeftoverRows { get; } = new();

    /// <summary>The shared and gate-protected leftovers: shown inline with their reason, never executed.</summary>
    public ObservableCollection<PlanRow> LeftoverSkipped { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand ScanLeftoversCommand { get; }
    public ICommand RemoveAppxCommand { get; }
    public ICommand UninstallSelectedCommand { get; }
    public ICommand OpenLocationCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>The command-bar scope filter: 0 = All, 1 = Desktop only, 2 = Microsoft Store only. It is an
    /// inventory filter — inspectable behaviour, not a mode name (A-M3-1).</summary>
    public int ScopeIndex
    {
        get => _scopeIndex;
        set
        {
            if (SetField(ref _scopeIndex, value))
            {
                AppsView.Refresh();
                OnPropertyChanged(nameof(IsScopeAll));
                OnPropertyChanged(nameof(IsScopeDesktop));
                OnPropertyChanged(nameof(IsScopeStore));
            }
        }
    }

    public bool IsScopeAll => _scopeIndex == 0;
    public bool IsScopeDesktop => _scopeIndex == 1;
    public bool IsScopeStore => _scopeIndex == 2;

    public bool IsLoading { get => _isLoading; private set => SetField(ref _isLoading, value); }

    /// <summary>True while a plan is executing (or a scan is in flight) — disables the run buttons.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                OnPropertyChanged(nameof(State));
        }
    }

    /// <summary>The screen's machine state, typed rather than a rendered string (decision §2.1 StatePill).</summary>
    public StatePillState State => IsBusy
        ? StatePillState.Running
        : HasResult ? StatePillState.Done : StatePillState.Preview;

    /// <summary>The localized label for <see cref="State"/>.</summary>
    public string StateText => I18n[State switch
    {
        StatePillState.Running => "uninstall.state.running",
        StatePillState.Done => "uninstall.state.done",
        _ => "uninstall.state.preview",
    }];

    /// <summary>
    /// Whether rows show their technical detail line. It ADDS detail and nothing else: it is not an input to
    /// any plan, selection or count, which is what makes it a safe probe (SPEC §7-4).
    /// </summary>
    public bool TechnicalDetails
    {
        get => _technicalDetails;
        set => SetField(ref _technicalDetails, value);
    }

    public int AppxCount { get => _appxCount; private set => SetField(ref _appxCount, value); }

    /// <summary>Localized notice when classic-app discovery was incomplete or unavailable. It renders as an
    /// amber HealthChip: partial data is attention, not irreversibility (A-M3-6).</summary>
    public string InventoryNotice
    {
        get => _inventoryNotice;
        private set
        {
            if (SetField(ref _inventoryNotice, value))
                OnPropertyChanged(nameof(HasInventoryNotice));
        }
    }

    public bool HasInventoryNotice => !string.IsNullOrWhiteSpace(InventoryNotice);

    /// <summary>True when a plan is staged and the confirm panel should be shown.</summary>
    public bool RequiresConfirmation => _pendingKind != PendingKind.None;

    /// <summary>True once a plan has been approved-and-run at least once (drives the result panel).</summary>
    public bool HasResult
    {
        get => _hasResult;
        private set
        {
            if (SetField(ref _hasResult, value))
                OnPropertyChanged(nameof(State));
        }
    }

    /// <summary>The "{0} done · {1} skipped · {2} failed" line for the last run.</summary>
    public string ResultSummary { get => _resultSummary; private set => SetField(ref _resultSummary, value); }

    /// <summary>"SafetyGate left N protected/shared items untouched", or empty. A refusal that is not
    /// reported is a refusal the user cannot audit (honesty invariant §4-3).</summary>
    public string ProtectedNote
    {
        get => _protectedNote;
        private set
        {
            if (SetField(ref _protectedNote, value))
                OnPropertyChanged(nameof(HasProtectedNote));
        }
    }

    public bool HasProtectedNote => !string.IsNullOrEmpty(_protectedNote);

    /// <summary>
    /// A loud, non-destructive error surfaced when the (normally unreachable)
    /// <see cref="LeftoverPlanBuildException"/> fires — a shared/protected action reached the plan builder.
    /// Nothing is staged and nothing is deleted; the banner says so (fail-loud, never crash, never swallow).
    /// </summary>
    public string BuildError
    {
        get => _buildError;
        private set
        {
            if (SetField(ref _buildError, value))
                OnPropertyChanged(nameof(HasBuildError));
        }
    }

    public bool HasBuildError => !string.IsNullOrEmpty(_buildError);

    public string Search
    {
        get => _search;
        // Refresh the VIEW only — the backing _allRows is untouched, so a staged plan/selection survives typing.
        set { if (SetField(ref _search, value)) AppsView.Refresh(); }
    }

    /// <summary>
    /// The single row selected in the unified grid. Setting it routes to the desktop-app or Store-app preview
    /// path and resets the removal preview: a new selection can never inherit the previous app's scan.
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
            ResetRemovalPreview();

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanUninstallSelected));
            OnPropertyChanged(nameof(DetailTitle));
            OnPropertyChanged(nameof(DetailPublisher));
            OnPropertyChanged(nameof(DetailVersion));
            OnPropertyChanged(nameof(DetailLocation));
            OnPropertyChanged(nameof(HasDetailLocation));
            RaiseRemovalState();
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
    public bool HasDesktopSelection => _selectedApp is not null;

    /// <summary>
    /// True when the selected row can actually be removed: a Store app (single-shot removal), or a desktop
    /// app whose composed plan carries at least one action. A desktop app with no usable uninstaller AND no
    /// scanned leftover has nothing to run, so the door is disabled rather than opening on an empty plan —
    /// a destructive verb is visible but disabled until a current plan exists (decision §2.3).
    /// </summary>
    public bool CanUninstallSelected
        => _selectedAppx is not null || (_selectedApp is not null && _staging is { Plan.IsEmpty: false });

    // ---- The removal preview (the 292 px detail rail) ----

    /// <summary>True while the leftover scan is running.</summary>
    public bool IsScanningLeftovers
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
                OnPropertyChanged(nameof(HasScanned));
        }
    }

    /// <summary>True once a leftover scan has produced a result for the current selection.</summary>
    public bool HasScanned => _scan is not null;

    /// <summary>True when a scan can run now: a desktop app is selected and nothing else is in flight.</summary>
    public bool CanScanLeftovers => _selectedApp is not null && !IsBusy && !IsScanningLeftovers;

    /// <summary>
    /// The literal vendor command this removal would run — the real executable plus its real arguments, read
    /// off the STAGED action's typed fields, never a free-text description (honesty invariant §4-1). Empty
    /// when the app has no usable uninstaller. It is the same action instance the gate will show and the
    /// executor will run, so the rail cannot advertise a command the plan does not contain.
    /// </summary>
    public string VendorCommandLine
    {
        get
        {
            if (_staging?.Plan.Actions.OfType<CommandAction>().FirstOrDefault() is not { } command)
                return string.Empty;
            return command.Arguments.Count == 0
                ? command.FileName
                : command.FileName + " " + string.Join(" ", command.Arguments);
        }
    }

    public bool HasVendorCommand => !string.IsNullOrEmpty(VendorCommandLine);

    /// <summary>True when the selected desktop app has NO usable vendor uninstaller — stated plainly rather
    /// than left as a button that does nothing.</summary>
    public bool VendorCommandUnavailable => _selectedApp is not null && !HasVendorCommand;

    /// <summary>
    /// True when a System Restore point can really be created now (System Restore on for the system drive AND
    /// elevated). Probed per selection; when false the toggle is ABSENT and the honest reason is shown, so no
    /// disabled control implies a choice the machine cannot honour.
    /// </summary>
    public bool RestorePointAvailable => _restorePointCapability?.IsAvailable() ?? false;

    /// <summary>
    /// The restore-point choice. Meaningful only while <see cref="RestorePointAvailable"/>; it defaults ON,
    /// because the extra rollback layer is the safe default and it never escalates the confirm tier
    /// (<see cref="CreateRestorePointAction"/> is protective and tier-exempt).
    /// </summary>
    public bool RestorePointEnabled
    {
        get => _restorePointEnabled;
        set
        {
            // The choice IS part of the plan, so the preview recomposes over the SAME scan.
            if (SetField(ref _restorePointEnabled, value))
                RebuildStaging(_scan);
        }
    }

    /// <summary>The primary danger button's label, naming the app it will remove.</summary>
    public string RemoveButtonText => _selectedRow is null
        ? string.Empty
        : I18n.Format("uninstall.removal.button", _selectedRow.DisplayName);

    // ---- Lean, info-only detail-pane projections (UI decision §3 — identity, no plan dump). ----

    public string DetailTitle => _selectedRow?.DisplayName ?? string.Empty;
    public string DetailPublisher => _selectedRow?.Publisher ?? InstalledApp.Em;
    public string DetailVersion => _selectedRow?.Version ?? InstalledApp.Em;

    /// <summary>The install location for the detail pane ("Open install folder" target), or empty.</summary>
    public string DetailLocation =>
        _selectedApp?.InstallLocation ?? _selectedAppx?.InstallLocation ?? string.Empty;

    public bool HasDetailLocation => !string.IsNullOrWhiteSpace(DetailLocation);

    /// <summary>IWckStartupAware entry point: kick off the same read-only inventory load as today.</summary>
    public Task OnShellStartupAsync() => LoadAsync();

    public async Task LoadAsync()
    {
        int generation = ++_loadGeneration; // runs on the caller/UI thread, before the first await — deterministic ordering
        IsLoading = true;
        try
        {
            // Build a single flat, name-sorted list of desktop + Store rows (UI decision §2). Reads + projection off-thread.
            var rows = await Task.Run(() =>
            {
                InstalledAppReadResult inventory = _appReader.ReadAllWithStatus();
                AppxReadResult appxInventory = _appxReader.ReadCurrentUserPackagesWithStatus();
                var apps = inventory.Apps
                    .Where(a => !a.IsSystemComponent)
                    .Select(AppRow.FromApp);
                var packages = appxInventory.Packages
                    .Where(p => !p.IsFrameworkOrSystem)
                    .Select(AppRow.FromAppx);
                var loadedRows = apps.Concat(packages)
                    .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                return (Rows: loadedRows, Status: CombineInventoryStatus(inventory.Status, appxInventory.Status));
            });

            // G2: discard a superseded load. A newer LoadAsync incremented _loadGeneration while this one was in
            // flight; committing these rows would clobber/duplicate the newer load's results. Do NOT clear or append.
            if (generation != _loadGeneration)
                return;

            SelectedRow = null;
            _allRows.Clear();
            InventoryNotice = rows.Status switch
            {
                InstalledAppReadStatus.Partial => I18n["uninstall.inventory.partial"],
                InstalledAppReadStatus.Unavailable => I18n["uninstall.inventory.unavailable"],
                _ => string.Empty,
            };
            AppxCount = rows.Rows.Count(r => r.IsStore);
            foreach (var r in rows.Rows)
            {
                r.BadgeText = LocalizeBadge(r.StatusBadge);
                _allRows.Add(r);
            }
            AppsView.Refresh();
        }
        finally
        {
            // Only the newest load owns the loading flag; a superseded load must not clear it out from under the winner.
            if (generation == _loadGeneration)
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

    private static InstalledAppReadStatus CombineInventoryStatus(
        InstalledAppReadStatus classicStatus,
        AppxReadStatus appxStatus)
    {
        if (classicStatus == InstalledAppReadStatus.Complete && appxStatus == AppxReadStatus.Complete)
            return InstalledAppReadStatus.Complete;
        if (classicStatus == InstalledAppReadStatus.Unavailable && appxStatus == AppxReadStatus.Unavailable)
            return InstalledAppReadStatus.Unavailable;
        return InstalledAppReadStatus.Partial;
    }

    /// <summary>The ICollectionView <c>Filter</c> predicate — pure, never mutates the source (UI decision §2).</summary>
    private bool MatchesSearch(object item)
    {
        if (item is not AppRow row)
            return false;

        // Scope: 1 = Desktop only, 2 = Microsoft Store only, 0 = All.
        if (_scopeIndex == 1 && row.IsStore)
            return false;
        if (_scopeIndex == 2 && !row.IsStore)
            return false;

        if (string.IsNullOrWhiteSpace(_search))
            return true;
        return CultureInfo.CurrentCulture.CompareInfo.IndexOf(
            row.SearchKey,
            _search.Trim(),
            CompareOptions.IgnoreCase) >= 0;
    }

    private void OpenSelectedLocation()
    {
        // Benign read-only UI affordance — goes through the sanctioned IFolderOpener (validates the directory,
        // pins Explorer to its System path), NEVER a direct Process.Start (banned API / spec §3).
        string path = DetailLocation;
        if (!string.IsNullOrWhiteSpace(path))
            _folderOpener.OpenFolder(path);
    }

    // ---- The leftover scan (ONE action, no depth modes) ----

    private async Task ScanLeftoversAsync()
    {
        if (_selectedApp is not { } app)
            return;

        // Drop the previous answer BEFORE the scan starts. While one is in flight the screen must not still
        // present the last result, and clearing here keeps HasScanned false for the whole window instead of
        // letting it flip true halfway through.
        RebuildStaging(scan: null);
        IsScanningLeftovers = true;
        try
        {
            var scanner = new LeftoverScanner(_probe, _gate);
            LeftoverScanResult result = await Task.Run(() => scanner.Scan(app, DateTime.UtcNow));
            RebuildStaging(result);
        }
        finally
        {
            // Last, and deliberately so: this is the signal a caller may wait on, and by the time it flips
            // every piece of state it implies has already been committed and announced.
            IsScanningLeftovers = false;
            RaiseRunCommandStates();
        }
    }

    /// <summary>
    /// Composes the ONE staging for <paramref name="scan"/> and shows its rows in the detail rail. The rail
    /// previews the very <see cref="PlanRow"/> INSTANCES the gate will hand to
    /// <see cref="PlanSelection.Subset"/>, so what the user reads before clicking and what they approve
    /// afterwards cannot be two different lists.
    /// <para>
    /// <b>The scan result is PUBLISHED LAST, after the rows it justifies.</b> <see cref="HasScanned"/> is
    /// derived from <see cref="_scan"/>, so assigning it first opened a window in which the screen truthfully
    /// answered "yes, I have scanned" while <see cref="LeftoverRows"/> was still empty — long enough for the
    /// rail to render "The scan found nothing left behind by this program." over a scan that found plenty,
    /// and long enough for a reader on another thread to act on it. The scan is therefore passed in rather
    /// than read out of the field: a caller cannot publish it early if it never holds it.
    /// </para>
    /// </summary>
    private void RebuildStaging(LeftoverScanResult? scan)
    {
        RemovalStaging? staging = null;
        string buildError = string.Empty;

        if (_selectedApp is { } app)
        {
            try
            {
                staging = _composer.Compose(app, scan, RestorePointAvailable && RestorePointEnabled,
                    DateTime.UtcNow);
            }
            catch (LeftoverPlanBuildException ex)
            {
                // FAIL-LOUD, NOT CRASH: the ProgramOwned-only guard fired. Nothing is staged and nothing is
                // deleted; the banner names the item that tripped it.
                buildError = I18n.Format("uninstall.removal.buildError", ex.ActionTarget);
            }
        }

        // Commit: rows and plan first, THEN the fact that a scan exists. Every announcement below describes
        // state that is already readable.
        ClearLeftoverRows();
        if (staging is { } composed)
        {
            foreach (PlanRow row in composed.LeftoverRows)
                LeftoverRows.Add(row);
            foreach (PlanRow row in composed.BlockedRows)
                LeftoverSkipped.Add(row);
        }

        _staging = staging;
        BuildError = buildError;
        _scan = scan;
        RaiseRemovalState();
    }

    private void ClearLeftoverRows()
    {
        LeftoverRows.Clear();
        LeftoverSkipped.Clear();
    }

    private void ResetRemovalPreview()
    {
        _pendingRemoval = null;
        // The safe default is ON where the capability exists; choosing the extra rollback layer never raises
        // the ceremony the user must clear, because the restore point is tier-exempt. Assigned to the field so
        // the setter's recompose does not fire before the selection has finished changing.
        _restorePointEnabled = RestorePointAvailable;
        OnPropertyChanged(nameof(RestorePointEnabled));
        // A new selection inherits no scan; RebuildStaging publishes that fact together with the empty rows.
        RebuildStaging(scan: null);
    }

    // ---- Stage (build plan + ask to confirm). Nothing executes here. ----

    /// <summary>
    /// The detail rail's single destructive door. A desktop app composes ONE plan — restore point, vendor
    /// uninstaller, leftovers — and opens the gate over it; a Store app keeps the existing single-shot
    /// irreversible removal (UI decision §4).
    /// </summary>
    private void StageSelected()
    {
        if (_selectedAppx is not null)
        {
            StageAppx();
            return;
        }
        if (_selectedApp is not null)
            StageRemoval();
    }

    private void StageRemoval()
    {
        if (_staging is not { } staging || staging.Plan.IsEmpty)
            return;

        _pendingKind = PendingKind.Removal;
        _pendingRemoval = staging;

        Gate.Open(
            ConfirmGateViewModel.TierFor(staging.Plan),
            I18n["uninstall.confirm.title"],
            I18n["uninstall.removal.confirm.body"],
            staging.Rows);
        RaiseConfirmationState();
    }

    private void StageAppx()
    {
        InstalledAppx? package = _selectedAppx;
        if (package is null)
            return;

        // Store app removal can't be undone, so it is always the IRREVERSIBLE tier (type-to-confirm).
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
        _pendingRemoval = null;
        Gate.Close();
        RaiseConfirmationState();
    }

    // ---- Approve (the ONLY place that runs a plan). ----

    private async Task ApproveAsync()
    {
        PendingKind kind = _pendingKind;
        InstalledAppx? package = _pendingAppx;
        RemovalStaging? staging = _pendingRemoval;

        if (kind == PendingKind.None)
            return;

        // The user has approved — clear the staged state and dismiss the confirm panel BEFORE we run, so the
        // result can never land before the confirm state is reset.
        _pendingKind = PendingKind.None;
        _pendingAppx = null;
        _pendingRemoval = null;
        Gate.Close();

        if (kind == PendingKind.Appx && package is null)
        {
            RaiseConfirmationState();
            return;
        }

        IsBusy = true;
        RaiseConfirmationState();
        try
        {
            if (kind == PendingKind.Appx)
                await RunAppxRemovalAsync(package!);
            else if (staging is not null)
                await RunRemovalAsync(staging);
        }
        finally
        {
            IsBusy = false;
            RaiseConfirmationState();
            RaiseRunCommandStates();
        }
    }

    /// <summary>
    /// Composes the plan the user actually approved and runs exactly that. The hash handed to the executor is
    /// the SUBSET's hash (SPEC §1.1): approving the vendor step plus one leftover cannot execute three,
    /// because the executor re-validates the approved hash and the two would not match.
    /// </summary>
    private async Task RunRemovalAsync(RemovalStaging staging)
    {
        ExecutionResults.Clear();
        ProtectedNote = string.Empty;

        OperationPlan approved = PlanSelection.Subset(staging.Plan, staging.Rows);
        if (approved.IsEmpty)
            return;

        string hash = approved.ComputeHash();
        PlanExecutionReport report = await Task.Run(() => _executor.ExecuteWithReport(approved, hash));

        int done = 0, skipped = 0, failed = 0;
        var byId = approved.Actions.ToDictionary(a => a.Id, DescribeResultRow);
        foreach (PlanActionResult result in report.Results)
        {
            string text = byId.TryGetValue(result.ActionId, out string? desc)
                ? desc
                : DescribeResultRowKind(result.Kind);

            // Anything but Done means this action did not complete — a DISPOSITION, not an irreversible act.
            // The level stays Info and the row's own blockedness carries the colour.
            bool ok = result.Status == PlanActionStatus.Done;
            if (ok)
                done++;
            else if (result.Status is PlanActionStatus.NotRun or PlanActionStatus.Skipped)
                skipped++;
            else
                failed++;

            ExecutionResults.Add(ResultRow(text, LocalizeStatus(result.Status), ok ? RiskLevel.Low : RiskLevel.Info,
                result.Detail, ok ? RowDisposition.Unstated : RowDisposition.WillNotRun));
        }

        // What the gate refused counts toward "skipped" and is stated, never buried (honesty invariant §4-3).
        int refused = LeftoverSkipped.Count;
        if (refused > 0)
        {
            skipped += refused;
            ProtectedNote = I18n.Format("uninstall.removal.protectedLine", refused);
        }

        ResultSummary = I18n.Format("uninstall.result.summary", done, skipped, failed);
        HasResult = true;
    }

    private async Task RunAppxRemovalAsync(InstalledAppx package)
    {
        ExecutionResults.Clear();
        ProtectedNote = string.Empty;

        // C1: route the per-user AppX removal through the SAME gated pipeline as every other destructive
        // action — a typed AppxRemoveAction, an OperationPlan, a plan hash, gate authorization, and
        // execution-time re-validation inside GatedExecutor. The VM never calls the raw remover.
        var action = new AppxRemoveAction
        {
            PackageFullName = package.PackageFullName,
            PackageFamilyName = package.PackageFamilyName ?? string.Empty,
            PackageDisplayName = package.DisplayName,
            IsFrameworkOrSystem = package.IsFrameworkOrSystem,
            Description = $"Remove Store app: {package.DisplayName}",
            Reason = "Per-user AppX removal (irreversible)",
            Risk = RiskLevel.Critical,
            Undo = UndoCapability.None,
        };
        var plan = new OperationPlan("Remove Store app", "uninstall",
            new PlannedAction[] { action }, DateTime.UtcNow);
        string hash = plan.ComputeHash();

        PlanExecutionReport report = await Task.Run(() => _executor.ExecuteWithReport(plan, hash));

        PlanActionResult result = report.Results.FirstOrDefault(r => r.ActionId == action.Id)
            ?? new PlanActionResult(action.Id, action.Kind, PlanActionStatus.NotRun, "no result");
        bool removed = result.Status == PlanActionStatus.Done;

        // A removal that did NOT happen is a disposition, not an irreversible act: the level stays Info and the
        // row's own blockedness carries the colour.
        ExecutionResults.Add(removed
            ? ResultRow($"Removed Store app: {package.DisplayName}", "Done", RiskLevel.Low, result.Detail)
            : ResultRow($"Store app not removed: {package.DisplayName}", "Failed", RiskLevel.Info, result.Detail,
                RowDisposition.WillNotRun));

        int done = removed ? 1 : 0;
        int failed = removed ? 0 : 1;
        ResultSummary = I18n.Format("uninstall.result.summary", done, 0, failed);
        HasResult = true;

        if (removed)
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

    /// <summary>
    /// The localized per-row status label — keyed by the <see cref="PlanActionStatus"/> value, NOT the English
    /// enum name, so the result rows read in the interface language.
    /// </summary>
    private string LocalizeStatus(PlanActionStatus status) => status switch
    {
        PlanActionStatus.Done => I18n["uninstall.result.status.done"],
        PlanActionStatus.Blocked => I18n["uninstall.result.status.blocked"],
        PlanActionStatus.Failed => I18n["uninstall.result.status.failed"],
        PlanActionStatus.NotRun => I18n["uninstall.result.status.notRun"],
        PlanActionStatus.Skipped => I18n["uninstall.result.status.notRun"],
        _ => status.ToString(),
    };

    /// <summary>
    /// The localized result-row text for a typed action — keyed by the action's shape/target, NOT the English
    /// Core <see cref="PlannedAction.Description"/>.
    /// </summary>
    private string DescribeResultRow(PlannedAction action) => action switch
    {
        CreateRestorePointAction => I18n["uninstall.result.row.restorePoint"],
        CommandAction => I18n.Format("uninstall.result.row.official", DetailTitle),
        RegistryDeleteAction r => I18n.Format("uninstall.result.row.registry",
            $"{r.Hive}\\{r.SubKeyPath}" + (r.ValueName is null ? string.Empty : "  ::  " + r.ValueName)),
        FileDeleteAction f => I18n.Format("uninstall.result.row.file", f.Path),
        ServiceDeleteAction s => I18n.Format("uninstall.result.row.service", s.ServiceName),
        TaskDeleteAction t => I18n.Format("uninstall.result.row.task", t.TaskPath),
        // Fall back to the machine Kind, localized — never the English Description.
        _ => DescribeResultRowKind(action.Kind),
    };

    /// <summary>Localized fallback row text when only the result's machine <c>Kind</c> is known.</summary>
    private string DescribeResultRowKind(string kind) => kind switch
    {
        "command" => I18n.Format("uninstall.result.row.official", DetailTitle),
        "registry.delete" => I18n["uninstall.result.row.registry.generic"],
        "file.delete" => I18n["uninstall.result.row.file.generic"],
        "service.delete" => I18n["uninstall.result.row.service.generic"],
        "task.delete" => I18n["uninstall.result.row.task.generic"],
        _ => kind,
    };

    private void RaiseConfirmationState()
    {
        OnPropertyChanged(nameof(RequiresConfirmation));
        OnPropertyChanged(nameof(IsBusy));
        Gate.RefreshBusy(); // keep the gate's Approve/Cancel enablement in step with IsBusy
    }

    private void RaiseRemovalState()
    {
        OnPropertyChanged(nameof(HasScanned));
        OnPropertyChanged(nameof(CanScanLeftovers));
        OnPropertyChanged(nameof(CanUninstallSelected));
        OnPropertyChanged(nameof(HasDesktopSelection));
        OnPropertyChanged(nameof(VendorCommandLine));
        OnPropertyChanged(nameof(HasVendorCommand));
        OnPropertyChanged(nameof(VendorCommandUnavailable));
        OnPropertyChanged(nameof(RestorePointAvailable));
        OnPropertyChanged(nameof(RestorePointEnabled));
        OnPropertyChanged(nameof(RemoveButtonText));
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("Item[]" or nameof(I18n.Culture) or nameof(I18n.SelectedCulture)))
            return;

        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(RemoveButtonText));
        foreach (AppRow row in _allRows)
            row.BadgeText = LocalizeBadge(row.StatusBadge);
    }

    /// <summary>Re-queries the run/uninstall commands' CanExecute after a selection or preview change.</summary>
    private static void RaiseRunCommandStates() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    /// <summary>Builds a result row reusing the same <see cref="PlanRow"/> shape.
    /// <paramref name="disposition"/> is how a row states that its subject did NOT happen; it defaults to
    /// <see cref="RowDisposition.Unstated"/> so a row that says nothing cannot assert something.</summary>
    private static PlanRow ResultRow(
        string text,
        string statusText,
        RiskLevel risk,
        string detail,
        RowDisposition disposition = RowDisposition.Unstated) => new()
    {
        Text = text,
        RiskText = statusText,
        Risk = risk,
        Undo = string.Empty,
        Detail = detail,
        Disposition = disposition,
    };
}

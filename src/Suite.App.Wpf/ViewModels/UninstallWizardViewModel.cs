using System.Collections.ObjectModel;
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
/// The 4-beat uninstall wizard (UI decision §4), a centered overlay opened by the detail-pane "Kaldır →".
/// It is net-new state on top of the EXISTING destructive pipeline — it never invents a second execution path:
///
/// <list type="number">
/// <item><b>Hazırlık &amp; Resmî kaldırıcı</b> — a restore-point toggle PRESENT-but-DISABLED with an honest
/// reason (PR-5 activates it). The dependable rollback is framed as the adapter-internal <c>.reg</c> backup +
/// the Recycle Bin, NOT a user toggle. "Resmî kaldırıcıyı çalıştır" stages the official-uninstaller plan via
/// <see cref="OfficialUninstallerPlanner"/> → <b>ConfirmGate #1</b> → <see cref="IExecutor"/> (GatedExecutor).</item>
/// <item><b>Tarama</b> — a scan-depth selector then <see cref="LeftoverScanner.Scan"/>.</item>
/// <item><b>Kalıntılar</b> — the 3-tier registry TreeView + a Files sub-tab bound to <see cref="LeftoverNode"/>s.
/// "Seçilenleri Sil" rebuilds the deletion plan via <see cref="LeftoverPlanBuilder"/> from the SELECTED
/// candidates → <b>ConfirmGate #2</b> → GatedExecutor. NO "İleri"/Next button ever deletes.</item>
/// <item><b>Sonuç</b> — the aggregated result summary + teal "SafetyGate korudu" lines + an honest
/// "Sistem geri yükleme kullanılamadı" line.</item>
/// </list>
///
/// HARD RULE (spec §6 + Hard rules): the deletion plan MUST be built by <see cref="LeftoverPlanBuilder"/> from
/// SELECTED candidates and flow through stage → ComputeHash → ConfirmGate → GatedExecutor — never the raw scan
/// candidates, never bypassing the gate. The single confirm surface is the reused <see cref="ConfirmGateViewModel"/>.
/// </summary>
public sealed class UninstallWizardViewModel : ObservableObject
{
    /// <summary>The four interaction beats (the step-rail also splits beat 1's two phases visually).</summary>
    public enum WizardBeat
    {
        /// <summary>Beat 1 — restore-point framing + run the official uninstaller (ConfirmGate #1).</summary>
        Prep = 0,

        /// <summary>Beat 2 — pick the scan depth and run the leftover scan.</summary>
        Scan = 1,

        /// <summary>Beat 3 — the 3-tier leftovers (registry + files); "Seçilenleri Sil" (ConfirmGate #2).</summary>
        Leftovers = 2,

        /// <summary>Beat 4 — the aggregated result.</summary>
        Result = 3,
    }

    /// <summary>Which sub-tab of the leftovers beat is shown.</summary>
    public enum LeftoverTab { Registry = 0, Files = 1 }

    /// <summary>Which staged run is awaiting confirmation — used to route Approve to the right plan.</summary>
    private enum PendingKind { None, Official, Leftovers }

    private readonly ISafetyGate _gate;
    private readonly ILeftoverProbe _probe;
    private readonly IExecutor _executor;
    private readonly IRestorePointCapabilityProbe? _restorePointCapability;
    private readonly Func<DateTime> _utcNow;

    private InstalledApp? _app;
    private bool _isOpen;
    private bool _isBusy;
    private WizardBeat _beat = WizardBeat.Prep;
    private LeftoverTab _activeTab = LeftoverTab.Registry;
    private int _scanDepthIndex = 1; // 0=Hızlı, 1=Standart (önerilen), 2=Derin
    private bool _hasScanned;
    private string _statusLine = string.Empty;
    private string _buildError = string.Empty;

    private bool _officialRan;
    private int _doneCount;
    private int _skippedCount;
    private int _failedCount;
    private bool _hasResult;

    private bool _restorePointAvailable;
    private bool _restorePointEnabled;

    // The plan currently staged for confirmation + the exact hash the user is approving.
    private OperationPlan? _pendingPlan;
    private string? _pendingPlanHash;
    private PendingKind _pendingKind;

    public UninstallWizardViewModel(I18n i18n, ISafetyGate gate, ILeftoverProbe probe, IExecutor executor,
        Func<DateTime>? utcNow = null, IRestorePointCapabilityProbe? restorePointCapability = null)
    {
        I18n = i18n;
        _gate = gate;
        _probe = probe;
        _executor = executor;
        _restorePointCapability = restorePointCapability;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

        // The reused 3-tier confirmation gate. BOTH ConfirmGate #1 (official) and #2 (leftovers) drive THIS
        // single instance — no new confirm dialog (Hard rules: ConfirmGate reuse).
        Gate = new ConfirmGateViewModel(
            i18n,
            onApprove: () => _ = ApproveAsync(),
            onCancel: CancelPending,
            isBusy: () => IsBusy);

        RunOfficialCommand = new RelayCommand(StageOfficial, () => CanRunOfficial);
        ScanCommand = new RelayCommand(async () => await ScanAsync(), () => CanScan);
        SkipToScanCommand = new RelayCommand(async () => await SkipToScanAsync(), () => CanSkipToScan);
        SelectAllOwnedCommand = new RelayCommand(SelectAllOwned, () => HasSelectableLeftovers && !IsBusy);
        DeleteSelectedCommand = new RelayCommand(StageLeftovers, () => HasCheckedLeftovers && !IsBusy);
        NextWithoutDeletingCommand = new RelayCommand(GoToResult, () => Beat == WizardBeat.Leftovers && !IsBusy);
        BackCommand = new RelayCommand(GoBack, () => CanGoBack);
        ShowRegistryTabCommand = new RelayCommand(() => ActiveTab = LeftoverTab.Registry);
        ShowFilesTabCommand = new RelayCommand(() => ActiveTab = LeftoverTab.Files);
        CloseCommand = new RelayCommand(Close, () => !IsBusy);
    }

    public I18n I18n { get; }

    /// <summary>The reused 3-tier confirmation gate (ConfirmGate #1 official + #2 leftovers).</summary>
    public ConfirmGateViewModel Gate { get; }

    /// <summary>The full registry-tier node list (ProgramOwned + Shared + Protected) for the TreeView.</summary>
    public ObservableCollection<LeftoverNode> RegistryNodes { get; } = new();

    /// <summary>The file/folder leftover nodes (same selectable rules; Recycle-Bin note).</summary>
    public ObservableCollection<LeftoverNode> FileNodes { get; } = new();

    /// <summary>The per-action outcome rows from the runs (official + leftovers), in order.</summary>
    public ObservableCollection<PlanRow> ExecutionResults { get; } = new();

    /// <summary>The teal "SafetyGate korudu" lines for the Protected candidates (result beat).</summary>
    public ObservableCollection<string> ProtectedLines { get; } = new();

    public ICommand RunOfficialCommand { get; }
    public ICommand ScanCommand { get; }

    /// <summary>
    /// "Taramaya geç →" — surfaced in the Prep beat ONLY for a broken-uninstaller app (its CanExecute requires
    /// the Prep beat, so it is not permanently disabled like <see cref="ScanCommand"/> would be). It transitions
    /// Prep → Scan and runs the leftover scan, so a broken-uninstaller app can still reach the leftover scan.
    /// </summary>
    public ICommand SkipToScanCommand { get; }

    public ICommand SelectAllOwnedCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand NextWithoutDeletingCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ShowRegistryTabCommand { get; }
    public ICommand ShowFilesTabCommand { get; }
    public ICommand CloseCommand { get; }

    // ---- Open / close ----

    /// <summary>True while the wizard overlay is shown.</summary>
    public bool IsOpen { get => _isOpen; private set => SetField(ref _isOpen, value); }

    /// <summary>The app being uninstalled (drives the summary header).</summary>
    public InstalledApp? App => _app;

    /// <summary>Open the wizard for <paramref name="app"/>, resetting all beat state.</summary>
    public void Open(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
        ResetState();
        // Probe restore-point capability once per open (SR enabled on the system drive AND elevated). When the
        // capability probe is absent (e.g. host tests not exercising PR-5), it stays unavailable + disabled.
        RestorePointAvailable = _restorePointCapability?.IsAvailable() ?? false;
        // When available, default the toggle ON — choosing the extra rollback layer is the safe default; it is
        // co-staged as a neighbor of the destructive plan and never escalates the tier (UI decision §5).
        RestorePointEnabled = RestorePointAvailable;
        IsOpen = true;
        OnPropertyChanged(nameof(AppTitle));
        OnPropertyChanged(nameof(AppMeta));
        RaiseAll();
    }

    private void Close()
    {
        Gate.Close();
        CancelPending();
        IsOpen = false;
    }

    private void ResetState()
    {
        Gate.Close();
        _pendingPlan = null;
        _pendingPlanHash = null;
        _pendingKind = PendingKind.None;
        Beat = WizardBeat.Prep;
        ActiveTab = LeftoverTab.Registry;
        ScanDepthIndex = 1;
        _hasScanned = false;
        _officialRan = false;
        _doneCount = _skippedCount = _failedCount = 0;
        _hasResult = false;
        RestorePointAvailable = false;
        RestorePointEnabled = false;
        StatusLine = string.Empty;
        BuildError = string.Empty;
        RegistryNodes.Clear();
        FileNodes.Clear();
        ExecutionResults.Clear();
        ProtectedLines.Clear();
    }

    // ---- Beat / busy state ----

    public WizardBeat Beat
    {
        get => _beat;
        private set
        {
            if (SetField(ref _beat, value))
                RaiseBeatDerived();
        }
    }

    public bool IsPrepBeat => Beat == WizardBeat.Prep;
    public bool IsScanBeat => Beat == WizardBeat.Scan;
    public bool IsLeftoversBeat => Beat == WizardBeat.Leftovers;
    public bool IsResultBeat => Beat == WizardBeat.Result;

    /// <summary>1-based step index for the step-rail highlight.</summary>
    public int StepNumber => (int)Beat + 1;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                Gate.RefreshBusy();
                RaiseAll();
            }
        }
    }

    /// <summary>The live "Şu an: …" status line shown during a run.</summary>
    public string StatusLine { get => _statusLine; private set => SetField(ref _statusLine, value); }

    /// <summary>
    /// A loud, non-destructive error surfaced when the (normally unreachable) <see cref="LeftoverPlanBuildException"/>
    /// fires — a Shared/Protected action reached the builder. The wizard does NOT crash and does NOT delete; it
    /// shows this banner and stages nothing (fix #8: fail-loud, not crash, never silently swallow).
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

    public LeftoverTab ActiveTab
    {
        get => _activeTab;
        set
        {
            if (SetField(ref _activeTab, value))
            {
                OnPropertyChanged(nameof(IsRegistryTab));
                OnPropertyChanged(nameof(IsFilesTab));
            }
        }
    }

    public bool IsRegistryTab => ActiveTab == LeftoverTab.Registry;
    public bool IsFilesTab => ActiveTab == LeftoverTab.Files;

    // ---- App summary projections ----

    public string AppTitle => _app?.DisplayName ?? string.Empty;

    public string AppMeta
    {
        get
        {
            if (_app is null)
                return string.Empty;
            string publisher = string.IsNullOrWhiteSpace(_app.Publisher) ? InstalledApp.Em : _app.Publisher!;
            string version = string.IsNullOrWhiteSpace(_app.DisplayVersion) ? InstalledApp.Em : _app.DisplayVersion!;
            return $"{publisher} · {version}";
        }
    }

    // ---- Beat 1: restore-point framing (PR-5 hook) ----

    /// <summary>
    /// True when a System Restore point can actually be created now — probed from
    /// <see cref="IRestorePointCapabilityProbe"/> (SR enabled on the system drive AND elevated) when the wizard
    /// opens (UI decision §5). When false, the toggle stays DISABLED with the honest reason. The capability is
    /// captured ONCE per Open so the UI is stable across a session.
    /// </summary>
    public bool RestorePointAvailable
    {
        get => _restorePointAvailable;
        private set
        {
            if (SetField(ref _restorePointAvailable, value))
            {
                OnPropertyChanged(nameof(RestorePointReason));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>
    /// The restore-point toggle's checked state (two-way). It is only meaningful while
    /// <see cref="RestorePointAvailable"/> is true; when available it defaults ON, so a co-staged restore point
    /// is prepended to the official-uninstaller plan (UI decision §5). Toggling it off opts out of the extra
    /// rollback layer — it never affects the destructive neighbor or its tier.
    /// </summary>
    public bool RestorePointEnabled
    {
        get => _restorePointEnabled;
        set => SetField(ref _restorePointEnabled, value);
    }

    /// <summary>
    /// The honest line under the toggle: when available, an honest "will be created before the operation"
    /// note; when unavailable, the "needs admin / SR off" reason (UI decision §5 — no fake guarantee).
    /// </summary>
    public string RestorePointReason => RestorePointAvailable
        ? I18n["uninstall.wizard.prep.restorePoint.reason.available"]
        : I18n["uninstall.wizard.prep.restorePoint.reason"];

    // ---- Scan depth (Beat 2) ----

    /// <summary>0 = Hızlı, 1 = Standart (önerilen), 2 = Derin (UI decision §4). Depth never loosens protection.</summary>
    public int ScanDepthIndex
    {
        get => _scanDepthIndex;
        set => SetField(ref _scanDepthIndex, value);
    }

    // ---- Command enablement ----

    public bool CanRunOfficial => IsPrepBeat && !IsBusy && !_officialRan
        && _app is not null && OfficialUninstallerPlanner.Build(_app, _utcNow()) is not null;

    /// <summary>True when the app has no usable uninstaller — the prep beat surfaces an honest "[Kaldırıcı Bozuk]" note.</summary>
    public bool OfficialUnavailable => _app is not null && OfficialUninstallerPlanner.Build(_app, _utcNow()) is null;

    public bool CanScan => IsScanBeat && !IsBusy && _app is not null;

    /// <summary>True in the Prep beat (the only place "Taramaya geç →" is shown) when a scan can run.</summary>
    public bool CanSkipToScan => IsPrepBeat && !IsBusy && _app is not null;

    public bool HasScanned => _hasScanned;

    /// <summary>True when at least one ProgramOwned (checkable) leftover exists — enables "Tümünü seç".</summary>
    public bool HasSelectableLeftovers =>
        RegistryNodes.Concat(FileNodes).Any(n => n.CanCheck);

    /// <summary>True when at least one node is currently checked — enables "Seçilenleri Sil".</summary>
    public bool HasCheckedLeftovers =>
        RegistryNodes.Concat(FileNodes).Any(n => n.CanCheck && n.IsChecked);

    public bool CanGoBack => (Beat == WizardBeat.Scan || (Beat == WizardBeat.Leftovers && _hasScanned)) && !IsBusy;

    /// <summary>The "{0} done · {1} skipped · {2} failed" line for the result beat.</summary>
    public string ResultSummary => I18n.Format("uninstall.result.summary", _doneCount, _skippedCount, _failedCount);

    public bool HasResult => _hasResult;

    public int OwnedCount => RegistryNodes.Concat(FileNodes).Count(n => n.CanCheck);
    public int CheckedCount => RegistryNodes.Concat(FileNodes).Count(n => n.CanCheck && n.IsChecked);

    /// <summary>"{0} programa-ait · {1} seçili" — the selection summary above the tree.</summary>
    public string SelectionSummary => I18n.Format("uninstall.wizard.selectionSummary", OwnedCount, CheckedCount);

    // ---- Beat 1: official uninstaller (ConfirmGate #1) ----

    private void StageOfficial()
    {
        if (_app is null)
            return;
        OperationPlan? plan = OfficialUninstallerPlanner.Build(_app, _utcNow());
        if (plan is null || plan.IsEmpty)
            return;

        // CO-STAGE the restore point (UI decision §5): when the user kept the toggle on AND SR is available,
        // PREPEND a protective CreateRestorePointAction so it runs BEFORE the destructive uninstaller and is
        // always a NEIGHBOR of it — never its own plan. The Irreversible tier still arises from the
        // uninstaller's Undo=None (the restore point is IsProtective → tier-exempt, UI decision §5).
        if (RestorePointAvailable && RestorePointEnabled)
            plan = WithCoStagedRestorePoint(plan);

        StatusLine = I18n["uninstall.wizard.status.officialStaged"];
        Stage(plan, PendingKind.Official, I18n["uninstall.wizard.confirm.official.body"]);
    }

    /// <summary>
    /// Build a new plan whose FIRST action is the protective <see cref="CreateRestorePointAction"/>, followed by
    /// the original (destructive) actions. The restore point is prepended so it is created before anything is
    /// removed; it is never staged alone (UI decision §5: "protective action is NEVER a standalone plan").
    /// </summary>
    private OperationPlan WithCoStagedRestorePoint(OperationPlan official)
    {
        var restorePoint = new CreateRestorePointAction
        {
            RestorePointName = I18n.Format("uninstall.wizard.restorePoint.name", AppTitle),
            Description = $"Create a System Restore point before uninstalling {AppTitle}",
            Reason = "Protective rollback layer co-staged with the official uninstaller (UI decision §5)",
            Risk = RiskLevel.Info,
            Undo = UndoCapability.None,
            // IsProtective is type-bound true on CreateRestorePointAction (no longer a settable flag — PR-5 FIX 1).
        };

        var actions = new List<PlannedAction>(official.Actions.Count + 1) { restorePoint };
        actions.AddRange(official.Actions);
        return new OperationPlan(official.Title, official.ModuleName, actions, _utcNow());
    }

    // ---- Beat 2: leftover scan ----

    /// <summary>
    /// "Taramaya geç →" (Prep beat) — skip the broken/unavailable official uninstaller and go straight to the
    /// leftover scan. Transitions Prep → Scan first (so the scan runs in its own beat), then runs the scan,
    /// which advances to the Leftovers beat. Without this, a broken-uninstaller app could never reach the scan
    /// (the shared <see cref="ScanCommand"/>'s CanExecute requires the Scan beat).
    /// </summary>
    private async Task SkipToScanAsync()
    {
        if (_app is null || !IsPrepBeat)
            return;
        Beat = WizardBeat.Scan;
        RaiseAll();
        await ScanAsync();
    }

    private async Task ScanAsync()
    {
        if (_app is null)
            return;

        IsBusy = true;
        StatusLine = I18n["uninstall.wizard.status.scanning"];
        try
        {
            InstalledApp app = _app;
            var scanner = new LeftoverScanner(_probe, _gate);
            LeftoverScanResult result = await Task.Run(() => scanner.Scan(app, _utcNow()));

            BuildNodes(result);
            _hasScanned = true;
            Beat = WizardBeat.Leftovers;
            StatusLine = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
        RaiseAll();
    }

    /// <summary>Project the classified candidates into checkable 3-tier nodes, split registry vs file.</summary>
    private void BuildNodes(LeftoverScanResult result)
    {
        RegistryNodes.Clear();
        FileNodes.Clear();

        foreach (LeftoverCandidate c in result.Candidates)
        {
            LeftoverNode node = MakeNode(c);
            // File/folder leftovers go to the Files sub-tab; registry/service/task go to the registry tree.
            if (c.Action is FileDeleteAction)
                FileNodes.Add(node);
            else
                RegistryNodes.Add(node);
        }
    }

    private LeftoverNode MakeNode(LeftoverCandidate c)
    {
        var (target, sub) = DescribeNode(c);
        return new LeftoverNode(c, target, sub);
    }

    /// <summary>The node's primary line (typed target) + its tier subline.</summary>
    private (string Target, string Sub) DescribeNode(LeftoverCandidate c)
    {
        string target = c.Action switch
        {
            RegistryDeleteAction r => $"{r.Hive}\\{r.SubKeyPath}"
                + (r.ValueName is null ? string.Empty : "  ::  " + r.ValueName),
            FileDeleteAction f => f.Path,
            ServiceDeleteAction s => $"Service: {s.ServiceName}",
            TaskDeleteAction t => $"Task: {t.TaskPath}",
            _ => c.Action.Description,
        };

        string sub = c.Classification switch
        {
            LeftoverClassification.ProgramOwned =>
                I18n.Format("uninstall.wizard.sub.owned", AppTitle),
            LeftoverClassification.Shared =>
                I18n["uninstall.wizard.sub.shared"],
            // Protected: render the gate's reason as the "SafetyGate korudu" line.
            _ => I18n.Format("uninstall.wizard.sub.protected", c.GateReason),
        };
        return (target, sub);
    }

    // ---- Beat 3: "Tümünü seç" (ProgramOwned only) + "Seçilenleri Sil" (ConfirmGate #2) ----

    /// <summary>
    /// "Tümünü seç" — scoped to ProgramOwned ONLY. A Shared/Protected node's <see cref="LeftoverNode.IsChecked"/>
    /// setter is a no-op, so toggling here can never check a non-owned row (spec §6).
    /// </summary>
    private void SelectAllOwned()
    {
        // Toggle: if every checkable node is already checked, clear; otherwise select all checkable.
        var checkable = RegistryNodes.Concat(FileNodes).Where(n => n.CanCheck).ToList();
        bool allChecked = checkable.Count > 0 && checkable.All(n => n.IsChecked);
        foreach (LeftoverNode n in checkable)
            n.IsChecked = !allChecked;
        RaiseSelectionState();
    }

    /// <summary>
    /// "Seçilenleri Sil" — rebuild the deletion plan from the SELECTED candidates via
    /// <see cref="LeftoverPlanBuilder"/> (the load-bearing binding), then stage → ConfirmGate #2.
    /// Only ProgramOwned-checkable nodes can be selected, so the builder only ever sees ProgramOwned actions.
    /// </summary>
    private void StageLeftovers() => StageLeftovers(null);

    /// <summary>
    /// The body of "Seçilenleri Sil". <paramref name="candidateOverride"/> exists ONLY for the
    /// defense-in-depth guard test (fix #6): it lets a test force a selected non-ProgramOwned candidate into the
    /// builder, bypassing the UI's CanCheck filter, to prove the builder guard — not just the UI filter — is what
    /// keeps Shared/Protected out of the plan. Production always passes null (the node projection); the
    /// <see cref="DeleteSelectedCommand"/> never reaches this overload with an override.
    /// </summary>
    public void StageLeftovers(IReadOnlyList<LeftoverCandidate>? candidateOverride)
    {
        if (_app is null)
            return;

        // Project every node back to a candidate carrying its Selected choice (non-checkable → Selected=false).
        IReadOnlyList<LeftoverCandidate> candidates = candidateOverride
            ?? RegistryNodes.Concat(FileNodes).Select(n => n.ToCandidate()).ToList();

        BuildError = string.Empty;
        OperationPlan plan;
        try
        {
            // BUILD VIA THE PLAN BUILDER — never the raw scan plan. Throws if a non-ProgramOwned slips through.
            plan = new LeftoverPlanBuilder().Build(_app, candidates, _utcNow());
        }
        catch (LeftoverPlanBuildException ex)
        {
            // FAIL-LOUD, NOT CRASH (fix #8): the guard fired (a Shared/Protected action reached the builder).
            // Surface it as a visible, non-destructive banner — never an unhandled WPF dispatcher exception,
            // and never silently swallowed. Nothing is staged or executed.
            BuildError = I18n.Format("uninstall.wizard.buildError", ex.ActionTarget);
            StatusLine = string.Empty;
            RaiseAll();
            return;
        }

        if (plan.IsEmpty)
            return;

        StatusLine = I18n["uninstall.wizard.status.leftoversStaged"];
        Stage(plan, PendingKind.Leftovers, I18n["uninstall.wizard.confirm.leftovers.body"]);
    }

    /// <summary>"İleri → (silmeden)" — advance to the result beat WITHOUT deleting anything (spec §4: never deletes).</summary>
    private void GoToResult()
    {
        FinalizeResult();
        Beat = WizardBeat.Result;
        RaiseAll();
    }

    private void GoBack()
    {
        Beat = Beat == WizardBeat.Leftovers ? WizardBeat.Scan : WizardBeat.Prep;
        RaiseAll();
    }

    // ---- Stage / confirm / run (the EXISTING pipeline; no second path) ----

    private void Stage(OperationPlan plan, PendingKind kind, string body)
    {
        _pendingPlan = plan;
        _pendingPlanHash = plan.ComputeHash(); // captured from the EXACT staged plan (spec §3)
        _pendingKind = kind;

        ConfirmTier tier = ConfirmGateViewModel.TierFor(plan);
        var rows = plan.Actions.Select(PlanRow.FromAction);
        Gate.Open(tier, I18n["uninstall.confirm.title"], body, rows);
        RaiseAll();
    }

    private void CancelPending()
    {
        if (_pendingKind == PendingKind.None)
            return;
        _pendingPlan = null;
        _pendingPlanHash = null;
        _pendingKind = PendingKind.None;
        StatusLine = string.Empty;
        Gate.Close();
        RaiseAll();
    }

    private async Task ApproveAsync()
    {
        if (_pendingKind == PendingKind.None || _pendingPlan is null || _pendingPlanHash is null)
            return;

        PendingKind kind = _pendingKind;
        OperationPlan plan = _pendingPlan;
        string hash = _pendingPlanHash;

        _pendingPlan = null;
        _pendingPlanHash = null;
        _pendingKind = PendingKind.None;
        IsBusy = true;
        Gate.Close();
        StatusLine = kind == PendingKind.Official
            ? I18n["uninstall.wizard.status.officialRunning"]
            : I18n["uninstall.wizard.status.leftoversRunning"];
        RaiseAll();
        try
        {
            ExecutionReport report = await Task.Run(() => _executor is GatedExecutor gated
                ? gated.ExecuteWithReport(plan, hash)
                : ToReport(_executor.Execute(plan, hash), plan));

            Accumulate(report, plan);

            if (kind == PendingKind.Official)
            {
                _officialRan = true;
                // After the official uninstaller, advance to the scan beat.
                Beat = WizardBeat.Scan;
                StatusLine = string.Empty;
            }
            else
            {
                // Leftovers ran → finalize the result beat.
                FinalizeResult();
                Beat = WizardBeat.Result;
                StatusLine = string.Empty;
            }
        }
        finally
        {
            IsBusy = false;
        }
        RaiseAll();
    }

    /// <summary>Fold one report's per-action outcomes into the running counts + result rows.</summary>
    private void Accumulate(ExecutionReport report, OperationPlan plan)
    {
        // Render each result row from a localized i18n key chosen by the action's typed shape — NEVER the Core
        // English a.Description (spec §4: Turkish chrome must not echo English Core text).
        var byId = plan.Actions.ToDictionary(a => a.Id, DescribeResultRow);
        foreach (ActionResult r in report.Results)
        {
            string text = byId.TryGetValue(r.ActionId, out var desc) ? desc : DescribeResultRowKind(r.Kind);
            RiskLevel risk = r.Status switch
            {
                ActionStatus.Done => RiskLevel.Low,
                ActionStatus.NotRun => RiskLevel.Info,
                _ => RiskLevel.Critical,
            };
            if (r.Status == ActionStatus.Done)
                _doneCount++;
            else if (r.Status == ActionStatus.NotRun)
                _skippedCount++;
            else
                _failedCount++;

            ExecutionResults.Add(new PlanRow
            {
                Text = text,
                RiskText = LocalizeStatus(r.Status),
                RiskBrush = RiskVisuals.For(risk),
                Undo = string.Empty,
                Detail = r.Detail,
            });
        }
    }

    /// <summary>
    /// The localized (tr/en) per-row status label — keyed by the <see cref="ActionStatus"/> value, NOT the
    /// English enum name, so the result rows read consistently with the Turkish summary (spec §4 i18n).
    /// </summary>
    private string LocalizeStatus(ActionStatus status) => status switch
    {
        ActionStatus.Done => I18n["uninstall.result.status.done"],
        ActionStatus.Blocked => I18n["uninstall.result.status.blocked"],
        ActionStatus.Failed => I18n["uninstall.result.status.failed"],
        ActionStatus.NotRun => I18n["uninstall.result.status.notRun"],
        _ => status.ToString(),
    };

    /// <summary>
    /// The localized result-row text for a typed action — keyed by the action's shape/target, NOT the English
    /// Core <see cref="PlannedAction.Description"/> (spec §4 / ax_pro + Claude-council i18n fix).
    /// </summary>
    private string DescribeResultRow(PlannedAction action) => action switch
    {
        CommandAction => I18n.Format("uninstall.wizard.row.official", AppTitle),
        RegistryDeleteAction r => I18n.Format("uninstall.wizard.row.registry",
            $"{r.Hive}\\{r.SubKeyPath}" + (r.ValueName is null ? string.Empty : "  ::  " + r.ValueName)),
        FileDeleteAction f => I18n.Format("uninstall.wizard.row.file", f.Path),
        ServiceDeleteAction s => I18n.Format("uninstall.wizard.row.service", s.ServiceName),
        TaskDeleteAction t => I18n.Format("uninstall.wizard.row.task", t.TaskPath),
        // Fall back to the machine Kind, localized — never the English Description.
        _ => DescribeResultRowKind(action.Kind),
    };

    /// <summary>Localized fallback row text when only the result's machine <c>Kind</c> is known.</summary>
    private string DescribeResultRowKind(string kind) => kind switch
    {
        "command" => I18n.Format("uninstall.wizard.row.official", AppTitle),
        "registry.delete" => I18n["uninstall.wizard.row.registry.generic"],
        "file.delete" => I18n["uninstall.wizard.row.file.generic"],
        "service.delete" => I18n["uninstall.wizard.row.service.generic"],
        "task.delete" => I18n["uninstall.wizard.row.task.generic"],
        _ => kind,
    };

    /// <summary>
    /// Compute the result-beat extras: the teal "SafetyGate korudu" lines (protected candidates count toward the
    /// honest "skipped/protected" framing) and mark the result ready.
    /// </summary>
    private void FinalizeResult()
    {
        ProtectedLines.Clear();
        int protectedCount = RegistryNodes.Concat(FileNodes).Count(n => n.IsProtected);
        if (protectedCount > 0)
        {
            ProtectedLines.Add(I18n.Format("uninstall.wizard.result.protectedLine", protectedCount));
            // Protected items the gate kept untouched count toward "skipped" for the honest summary.
            _skippedCount += protectedCount;
        }
        _hasResult = true;
    }

    // ---- helpers ----

    private static ExecutionReport ToReport(ExecutionOutcome outcome, OperationPlan plan)
    {
        ActionStatus status = outcome.Ran ? ActionStatus.Done : ActionStatus.NotRun;
        var results = plan.Actions
            .Select(a => new ActionResult(a.Id, a.Kind, status, outcome.Reason))
            .ToArray();
        return new ExecutionReport(outcome.Ran, plan.ComputeHash(), results);
    }

    /// <summary>Re-raise the selection-derived state (call after a per-row check changes).</summary>
    public void RaiseSelectionState()
    {
        OnPropertyChanged(nameof(HasSelectableLeftovers));
        OnPropertyChanged(nameof(HasCheckedLeftovers));
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(OwnedCount));
        OnPropertyChanged(nameof(SelectionSummary));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void RaiseBeatDerived()
    {
        OnPropertyChanged(nameof(IsPrepBeat));
        OnPropertyChanged(nameof(IsScanBeat));
        OnPropertyChanged(nameof(IsLeftoversBeat));
        OnPropertyChanged(nameof(IsResultBeat));
        OnPropertyChanged(nameof(StepNumber));
        OnPropertyChanged(nameof(CanGoBack));
    }

    private void RaiseAll()
    {
        RaiseBeatDerived();
        RaiseSelectionState();
        OnPropertyChanged(nameof(CanRunOfficial));
        OnPropertyChanged(nameof(OfficialUnavailable));
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanSkipToScan));
        OnPropertyChanged(nameof(HasScanned));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(HasBuildError));
        OnPropertyChanged(nameof(BuildError));
        OnPropertyChanged(nameof(RestorePointAvailable));
        OnPropertyChanged(nameof(RestorePointEnabled));
        OnPropertyChanged(nameof(RestorePointReason));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}

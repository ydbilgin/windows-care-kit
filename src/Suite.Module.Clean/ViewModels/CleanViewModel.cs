using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using WindowsCareKit.App.Controls;
using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Module.Clean.ViewModels;

/// <summary>
/// The Temizle (Clean) view-model. Four read-only sections feed ONE approval door: build a dry-run
/// <see cref="OperationPlan"/> → the user vetoes what they do not want, per row → approve →
/// <see cref="PlanSelection.Subset"/> composes the plan they actually approved →
/// <c>hash = subset.ComputeHash()</c> → the UI plan executor. Nothing runs without an explicit approve, and
/// never outside the executor (spec §3). Command enable/disable is re-queried automatically by WPF's
/// <c>CommandManager</c> (the <see cref="RelayCommand"/> wires <c>RequerySuggested</c>).
/// <para>
/// Every count on this screen is arithmetic over typed state — <see cref="PlanRow.Selection"/> and
/// <see cref="PlanRow.Recovery"/> — never over rendered text (SPEC §1.2, §2.2). That is what makes the review
/// rail invariant under a language switch.
/// </para>
/// </summary>
public sealed class CleanViewModel : ObservableObject
{
    private readonly IJunkProbe _junkProbe;
    private readonly IStartupProbe _startupProbe;
    private readonly IBrowserExtensionInventory _extensions;
    private readonly IRecycleBinService _recycleBin;
    private readonly IFolderOpener _folderOpener;
    private readonly ISafetyGate _gate;
    private readonly IPlanExecutor _executor;

    /// <summary>
    /// The ONE recycle-bin action instance on this screen. It is the action <see cref="RecycleRow"/> previews,
    /// the action the dedicated red button stages, and the action the approvable plan carries — one instance,
    /// because <see cref="PlanSelection.Subset"/> matches rows to plan occurrences by REFERENCE and a second
    /// structurally-equal instance would be a different occurrence.
    /// </summary>
    private readonly EmptyRecycleBinAction _recycleAction = new()
    {
        Description = "Empty the Recycle Bin on all drives",
        Reason = "User approved irreversible Recycle Bin empty",
    };

    private readonly List<PlanRow> _attachedRows = new();

    private IReadOnlyList<PlannedAction> _junkActions = Array.Empty<PlannedAction>();
    private OperationPlan? _pendingPlan;
    private OperationPlan? _pendingStartupPlan;
    private OperationPlan? _stagedPlan;
    private bool _isBusy;
    private bool _junkScanned;
    private bool _recycleConfirmPending;
    private bool _technicalDetails = true;
    private StartupRow? _selectedStartup;
    private string _recycleStats = string.Empty;
    private string _resultSummary = string.Empty;
    private string _recycleHealthNote = string.Empty;
    private string _startupHealthNote = string.Empty;
    private string _extensionsHealthNote = string.Empty;

    public CleanViewModel(
        I18n i18n,
        IJunkProbe junkProbe,
        IStartupProbe startupProbe,
        IBrowserExtensionInventory extensions,
        IRecycleBinService recycleBin,
        IFolderOpener folderOpener,
        ISafetyGate gate,
        IPlanExecutor executor)
    {
        I18n = i18n;
        _junkProbe = junkProbe ?? throw new ArgumentNullException(nameof(junkProbe));
        _startupProbe = startupProbe ?? throw new ArgumentNullException(nameof(startupProbe));
        _extensions = extensions ?? throw new ArgumentNullException(nameof(extensions));
        _recycleBin = recycleBin ?? throw new ArgumentNullException(nameof(recycleBin));
        _folderOpener = folderOpener ?? throw new ArgumentNullException(nameof(folderOpener));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));

        RecycleRow = PlanRow.FromAction(_recycleAction, I18n, isVetoable: true);
        RecycleRow.IsIncluded = StartsIncluded(_recycleAction);
        Attach(RecycleRow);
        RebuildPendingPlan();

        ScanJunkCommand = new AsyncRelayCommand(ScanJunkAsync, () => !IsBusy);
        ApprovePlanCommand = new AsyncRelayCommand(
            ApprovePlanAsync,
            () => !IsBusy && !RecycleConfirmPending && SelectedActionCount > 0);
        LoadStartupCommand = new AsyncRelayCommand(LoadStartupAsync, () => !IsBusy);
        DisableStartupCommand = new AsyncRelayCommand(DisableStartupAsync, () => !IsBusy && _selectedStartup is not null);
        RefreshRecycleCommand = new AsyncRelayCommand(RefreshRecycleAsync, () => !IsBusy);
        // Emptying the bin is irreversible → stage a confirm first; the actual empty needs explicit approval.
        EmptyRecycleCommand = new RelayCommand(StageEmptyRecycle, () => !IsBusy && !RecycleConfirmPending);
        ConfirmEmptyRecycleCommand = new AsyncRelayCommand(ConfirmEmptyRecycleAsync, () => RecycleConfirmPending && !IsBusy);
        CancelEmptyRecycleCommand = new RelayCommand(CancelEmptyRecycle, () => RecycleConfirmPending);
        LoadExtensionsCommand = new AsyncRelayCommand(LoadExtensionsAsync, () => !IsBusy);
        OpenExtensionFolderCommand = new RelayCommand(p => OpenExtensionFolder(p as BrowserExtension), _ => !IsBusy);

        I18n.PropertyChanged += OnLocalizationChanged;
    }

    public I18n I18n { get; }

    // Junk
    public ObservableCollection<PlanRow> JunkPreview { get; } = new();
    public ObservableCollection<PlanRow> JunkSkipped { get; } = new();
    public ICommand ScanJunkCommand { get; }
    public bool JunkScanned { get => _junkScanned; private set => SetField(ref _junkScanned, value); }
    public bool JunkEmpty => _junkScanned && _junkActions.Count == 0;

    // Startup
    public ObservableCollection<StartupRow> StartupEntries { get; } = new();
    public ObservableCollection<PlanRow> StartupPreview { get; } = new();
    public ICommand LoadStartupCommand { get; }
    public ICommand DisableStartupCommand { get; }

    /// <summary>Non-empty when one or more startup sources could not be read — the list may be incomplete (NEW-07).</summary>
    public string StartupHealthNote { get => _startupHealthNote; private set => SetField(ref _startupHealthNote, value); }

    public StartupRow? SelectedStartup
    {
        get => _selectedStartup;
        set
        {
            if (SetField(ref _selectedStartup, value))
            {
                OnPropertyChanged(nameof(HasStartupSelection));
                BuildStartupPreview(value);
            }
        }
    }

    public bool HasStartupSelection => _selectedStartup is not null;

    // Recycle bin
    public ICommand RefreshRecycleCommand { get; }
    public ICommand EmptyRecycleCommand { get; }
    public ICommand ConfirmEmptyRecycleCommand { get; }
    public ICommand CancelEmptyRecycleCommand { get; }
    public string RecycleStats { get => _recycleStats; private set => SetField(ref _recycleStats, value); }

    /// <summary>
    /// The Recycle-Bin row: a real, vetoable <see cref="PlanRow"/> over the same action the red button stages,
    /// so the review rail counts it from typed state instead of from a second bespoke flag. It starts
    /// UNSELECTED and always will: its <see cref="PlanRow.Recovery"/> is <see cref="UndoCapability.None"/>, and
    /// nothing irreversible is ever pre-selected (honesty invariant §4-7, spec §2.1 rule 3).
    /// </summary>
    public PlanRow RecycleRow { get; }

    /// <summary>Non-empty when the recycle-bin query could not be completed — the totals are unknown, not zero (NEW-07).</summary>
    public string RecycleHealthNote { get => _recycleHealthNote; private set => SetField(ref _recycleHealthNote, value); }

    /// <summary>True while the irreversible empty-bin confirm panel is shown.</summary>
    public bool RecycleConfirmPending
    {
        get => _recycleConfirmPending;
        private set => SetField(ref _recycleConfirmPending, value);
    }

    // Browser extensions
    public ObservableCollection<BrowserExtension> Extensions { get; } = new();
    public ICommand LoadExtensionsCommand { get; }
    public ICommand OpenExtensionFolderCommand { get; }

    /// <summary>Non-empty when one or more browser profiles could not be read — the list may be incomplete (NEW-07).</summary>
    public string ExtensionsHealthNote { get => _extensionsHealthNote; private set => SetField(ref _extensionsHealthNote, value); }

    // ---- Review summary (the 292 px rail) ----

    /// <summary>
    /// The one destructive door on this screen: it composes <see cref="PlanSelection.Subset"/> over the rows
    /// the user was actually shown and executes exactly that. When the composed subset contains an
    /// irreversible action it stages the inline confirm instead of running — the proportional ceremony is
    /// preserved, never replaced.
    /// </summary>
    public ICommand ApprovePlanCommand { get; }

    /// <summary>The screen's machine state, typed rather than a rendered string, so the pill and any counter
    /// read the same value (decision §2.1 StatePill).</summary>
    public StatePillState State => IsBusy
        ? StatePillState.Running
        : HasResult ? StatePillState.Done : StatePillState.Preview;

    /// <summary>The localized label for <see cref="State"/>.</summary>
    public string StateText => I18n[State switch
    {
        StatePillState.Running => "clean.state.running",
        StatePillState.Done => "clean.state.done",
        _ => "clean.state.preview",
    }];

    /// <summary>
    /// Whether rows show their technical detail line. It ADDS detail and nothing else: it is not an input to
    /// any plan, selection or count (A-M2-7), which is what makes it a safe probe for "which fields are
    /// compact vs technical" (SPEC §7-4).
    /// </summary>
    public bool TechnicalDetails
    {
        get => _technicalDetails;
        set => SetField(ref _technicalDetails, value);
    }

    /// <summary>How many actions the composed plan would carry. Derived from <see cref="PlanRow.Selection"/>
    /// with exactly the survivor rule <see cref="PlanSelection.Subset"/> applies, so the number on screen and
    /// the number of actions handed to the executor cannot disagree (A-M2-3).</summary>
    public int SelectedActionCount
        => SelectableRows.Count(row => row.Selection is RowSelection.Required or RowSelection.OptionalIncluded);

    /// <summary>Rows the engine refused — they are shown, and they will not run.</summary>
    public int ProtectedCount => JunkSkipped.Count;

    public int RecoveryFullCount => CountRecovery(UndoCapability.Full);
    public int RecoveryPartialCount => CountRecovery(UndoCapability.Partial);
    public int RecoveryNoneCount => CountRecovery(UndoCapability.None);

    /// <summary>
    /// Selected rows whose recovery is genuinely UNKNOWN (spec §2.0b). It is reported separately and never
    /// folded into <see cref="RecoveryNoneCount"/>: collapsing unknown into "permanent" is fail-safe but not
    /// honest, and the row's own rendered text may claim otherwise.
    /// </summary>
    public int RecoveryUnknownCount => SelectedRows().Count(row => row.Recovery is null);

    /// <summary>One sentence about what approving would cost. It states only what the typed recovery values
    /// say — never a claim about the health of the machine (A-M2-8, honesty invariant §4-6).</summary>
    public string ConsequenceSentence => SelectedActionCount == 0
        ? I18n["clean.consequence.empty"]
        : RecoveryNoneCount > 0
            ? I18n.Format("clean.consequence.permanent", RecoveryNoneCount)
            : I18n["clean.consequence.reversible"];

    /// <summary>The approve button's label, carrying the count so the button states what it will do.</summary>
    public string ApproveText => I18n.Format("clean.review.approve", SelectedActionCount);

    // Shared
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                OnPropertyChanged(nameof(State));
        }
    }

    public string ResultSummary
    {
        get => _resultSummary;
        private set
        {
            if (SetField(ref _resultSummary, value))
            {
                OnPropertyChanged(nameof(HasResult));
                OnPropertyChanged(nameof(State));
            }
        }
    }

    /// <summary>True once an execution/empty has produced a summary line, so the result banner can show.</summary>
    public bool HasResult => !string.IsNullOrEmpty(_resultSummary);

    // ---- Selection ----

    /// <summary>Every row that names an action of the approvable plan, in PLAN order.</summary>
    private IEnumerable<PlanRow> SelectableRows => JunkPreview.Prepend(RecycleRow);

    /// <summary>Every row the user was shown for the approvable plan, including the blocked ones. The blocked
    /// rows are passed to <see cref="PlanSelection.Subset"/> deliberately: a blocked row can only ever
    /// SUBTRACT, so handing them over makes the composed plan agree with the whole screen rather than with the
    /// part of it that happens to be runnable.</summary>
    private IEnumerable<PlanRow> ApprovalRows => SelectableRows.Concat(JunkSkipped);

    private IEnumerable<PlanRow> SelectedRows()
        => SelectableRows.Where(row => row.Selection is RowSelection.Required or RowSelection.OptionalIncluded);

    private int CountRecovery(UndoCapability recovery)
        => SelectedRows().Count(row => row.Recovery == recovery);

    /// <summary>
    /// Default inclusion (spec §2.1 rule 3). The safe end is fixed and absolute: anything best-effort and
    /// anything with no undo starts EXCLUDED, always. Every junk delete the engine produces is best-effort
    /// (<c>JunkScanner</c> sets <c>BestEffort</c> on all of them so one locked temp file cannot abort the
    /// sweep), so on this screen the rule resolves to "nothing is pre-checked" — which is also honesty
    /// invariant §4-7 read literally.
    /// </summary>
    private static bool StartsIncluded(PlannedAction action)
        => action.Undo != UndoCapability.None
           && action is not FileDeleteAction { BestEffort: true };

    /// <summary>
    /// The approvable plan. The recycle-bin empty is FIRST on purpose: action order is an execution-safety
    /// property, and a bin emptied AFTER the junk deletes would destroy the very Recycle-Bin copies those
    /// deletes promise as their undo. Emptying first removes only what the user already had in the bin, and
    /// every junk delete in the same run stays recoverable — so the row's stated recovery is still true after
    /// the run, which is the whole point of stating it.
    /// </summary>
    private void RebuildPendingPlan()
    {
        var actions = new List<PlannedAction>(_junkActions.Count + 1) { _recycleAction };
        actions.AddRange(_junkActions);
        _pendingPlan = new OperationPlan("Clean", "clean", actions, DateTime.UtcNow);
    }

    private void Attach(PlanRow row)
    {
        row.PropertyChanged += OnRowChanged;
        _attachedRows.Add(row);
    }

    private void DetachJunkRows()
    {
        foreach (PlanRow row in _attachedRows.Where(r => !ReferenceEquals(r, RecycleRow)).ToArray())
        {
            row.PropertyChanged -= OnRowChanged;
            _attachedRows.Remove(row);
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlanRow.Selection) or nameof(PlanRow.IsIncluded))
            RaiseSelectionCounts();
    }

    private void RaiseSelectionCounts()
    {
        OnPropertyChanged(nameof(SelectedActionCount));
        OnPropertyChanged(nameof(ProtectedCount));
        OnPropertyChanged(nameof(RecoveryFullCount));
        OnPropertyChanged(nameof(RecoveryPartialCount));
        OnPropertyChanged(nameof(RecoveryNoneCount));
        OnPropertyChanged(nameof(RecoveryUnknownCount));
        OnPropertyChanged(nameof(ConsequenceSentence));
        OnPropertyChanged(nameof(ApproveText));
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("Item[]" or nameof(I18n.Culture) or nameof(I18n.SelectedCulture)))
            return;

        OnPropertyChanged(nameof(StateText));

        // The counts are re-announced even though a language switch cannot move one. That is the point: an
        // invariant nothing re-reads is an invariant nothing can CATCH breaking. Leaving them un-raised kept
        // the screen showing values cached from before the switch, so a counter that parsed the localized undo
        // text would have gone wrong in the view model while the rail still displayed the old, correct number
        // — measured, 2026-08-06, and the reason this line exists.
        RaiseSelectionCounts();
    }

    // ---- Junk ----

    private async Task ScanJunkAsync()
    {
        IsBusy = true;
        DetachJunkRows();
        JunkPreview.Clear();
        JunkSkipped.Clear();
        try
        {
            var scanner = new JunkScanner(_junkProbe, _gate);
            JunkScanResult result = await Task.Run(() => scanner.Scan(DateTime.UtcNow));
            _junkActions = result.Plan.Actions;
            RebuildPendingPlan();

            foreach (PlannedAction a in _junkActions)
            {
                PlanRow row = PlanRow.FromAction(a, I18n, isVetoable: true);
                row.IsIncluded = StartsIncluded(a);
                Attach(row);
                JunkPreview.Add(row);
            }

            foreach (SkippedAction s in result.Skipped)
                JunkSkipped.Add(PlanRow.FromSkipped(s.Action, s.Reason, I18n));
        }
        finally
        {
            JunkScanned = true;
            OnPropertyChanged(nameof(JunkEmpty));
            RaiseSelectionCounts();
            IsBusy = false;
        }
    }

    /// <summary>
    /// Composes the plan the user actually approved and runs exactly that. The hash handed to the executor is
    /// the SUBSET's hash (spec §1.1): approving three actions cannot execute five, because the gate
    /// re-validates the approved hash and the two would not match.
    /// </summary>
    private async Task ApprovePlanAsync()
    {
        if (_pendingPlan is not { } source)
            return;

        OperationPlan subset = PlanSelection.Subset(source, ApprovalRows);
        if (subset.IsEmpty)
            return;

        if (subset.Actions.Any(a => a.Undo == UndoCapability.None))
        {
            // The approved set contains something irreversible → the inline confirm, not a silent run. The
            // ceremony is the shipped one (decision §2.1 calls it the correct proportional pattern); only what
            // it is staged with changed.
            _stagedPlan = subset;
            RecycleConfirmPending = true;
            return;
        }

        await RunPlanAsync(subset);
        await ScanJunkAsync();
    }

    /// <summary>Localized caution note for a non-Complete inventory source; empty string when Complete
    /// (so the bound HealthChip stays collapsed via NonEmptyToVisibleConverter). Partial and Unavailable both
    /// surface the same "may be incomplete" caution — the list is not to be trusted as a complete empty.</summary>
    private string IncompleteNote(SourceHealth health, string incompleteKey)
        => health == SourceHealth.Complete ? string.Empty : I18n[incompleteKey];

    // ---- Startup ----

    private async Task LoadStartupAsync()
    {
        IsBusy = true;
        StartupEntries.Clear();
        StartupPreview.Clear();
        SelectedStartup = null;
        StartupHealthNote = string.Empty;
        try
        {
            StartupInventory inventory = await Task.Run(() => _startupProbe.ReadAll());
            foreach (StartupEntry e in inventory.Entries)
                StartupEntries.Add(new StartupRow(e, I18n.Format("clean.startup.source", e.Source.ToString())));
            StartupHealthNote = IncompleteNote(inventory.Health, "clean.startup.incomplete");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BuildStartupPreview(StartupRow? row)
    {
        StartupPreview.Clear();
        _pendingStartupPlan = null;
        if (row is null)
            return;

        OperationPlan plan = StartupPlanner.BuildDisablePlan(row.Entry, DateTime.UtcNow);
        _pendingStartupPlan = plan;
        foreach (PlannedAction a in plan.Actions)
        {
            SafetyVerdict v = _gate.Evaluate(a);
            StartupPreview.Add(v.Allowed ? PlanRow.FromAction(a, I18n) : PlanRow.FromSkipped(a, v.Reason, I18n));
        }
    }

    private async Task DisableStartupAsync()
    {
        OperationPlan? plan = _pendingStartupPlan;
        if (plan is null || plan.IsEmpty)
            return;

        await RunPlanAsync(plan);
        await LoadStartupAsync();
    }

    // ---- Recycle bin ----

    private async Task RefreshRecycleAsync()
    {
        IsBusy = true;
        try
        {
            RecycleBinInventory inventory = await Task.Run(() => _recycleBin.Query());
            if (inventory.Health == SourceHealth.Complete && inventory.Stats is { } stats)
            {
                RecycleStats = I18n.Format("clean.recycle.stats", stats.ItemCount, JunkScanner.FormatBytes(stats.ApproxBytes));
                RecycleHealthNote = string.Empty;
            }
            else
            {
                RecycleStats = string.Empty;                       // never a fake zero (NEW-07)
                RecycleHealthNote = I18n["clean.recycle.unavailable"];
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Shows the confirm panel over the bin alone; nothing is emptied until the user approves it.</summary>
    private void StageEmptyRecycle()
    {
        _stagedPlan = new OperationPlan(
            "Empty Recycle Bin", "clean", new PlannedAction[] { _recycleAction }, DateTime.UtcNow);
        RecycleConfirmPending = true;
    }

    private void CancelEmptyRecycle()
    {
        _stagedPlan = null;
        RecycleConfirmPending = false;
    }

    private async Task ConfirmEmptyRecycleAsync()
    {
        OperationPlan? plan = _stagedPlan;
        _stagedPlan = null;
        RecycleConfirmPending = false;
        if (plan is null)
            return;

        try
        {
            await RunPlanAsync(plan);
        }
        finally
        {
            await RefreshRecycleAsync();
            if (JunkScanned)
                await ScanJunkAsync();
        }
    }

    // ---- Browser extensions (inventory only) ----

    private async Task LoadExtensionsAsync()
    {
        IsBusy = true;
        Extensions.Clear();
        ExtensionsHealthNote = string.Empty;
        try
        {
            BrowserExtensionListing listing = await Task.Run(() => _extensions.ReadAll());
            foreach (BrowserExtension ext in listing.Extensions)
                Extensions.Add(ext);
            ExtensionsHealthNote = IncompleteNote(listing.Health, "clean.ext.incomplete");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Opens the extension's folder in Explorer. This is a benign read-only UI affordance, NOT a gated
    /// destructive action, so it goes through <see cref="IFolderOpener"/> (which validates the directory
    /// and uses an argument list) — never the executor and never a direct <c>Process.Start</c> here
    /// (spec §3 "no silent execution through the destructive surface").
    /// </summary>
    private void OpenExtensionFolder(BrowserExtension? ext)
    {
        if (ext is null || string.IsNullOrWhiteSpace(ext.FolderPath))
            return;

        _folderOpener.OpenFolder(ext.FolderPath);
    }

    // ---- Shared execution ----

    /// <summary>The one execution path: hash the plan being executed, run it through the executor, show the
    /// report. The hash is computed from THIS plan — never from the superset it was composed out of, which is
    /// the TOCTOU contract the veto is enforced by (spec §1.1).</summary>
    private async Task RunPlanAsync(OperationPlan plan)
    {
        IsBusy = true;
        try
        {
            string hash = plan.ComputeHash();
            PlanExecutionSummary summary = await Task.Run(() => _executor.ExecuteWithSummary(plan, hash));
            ResultSummary = I18n.Format(
                "clean.result.summary",
                summary.DoneCount,
                summary.SkippedOrNotRunCount,
                summary.FailedCount);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>A startup entry as shown in the list, with a localized source label.</summary>
public sealed class StartupRow
{
    public StartupRow(StartupEntry entry, string sourceLabel)
    {
        Entry = entry;
        SourceLabel = sourceLabel;
    }

    public StartupEntry Entry { get; }
    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public string SourceLabel { get; }
}

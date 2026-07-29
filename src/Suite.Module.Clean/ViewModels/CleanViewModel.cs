using System.Collections.ObjectModel;
using System.Windows.Input;
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
/// The Temizle (Clean) view-model. Four read-only sections each feed the one execution path: build a
/// dry-run <see cref="OperationPlan"/> → the user previews it (risk-colored <see cref="PlanRow"/>) →
/// approve → <c>hash = plan.ComputeHash()</c> → the UI plan executor. Junk and
/// the recycle bin are the only destructive sections; startup is a per-entry disable plan; browser
/// extensions are inventory-only (removal is out of scope, spec §1.2). Nothing runs without an explicit
/// approve, and never outside the executor (spec §3). Command enable/disable is re-queried automatically
/// by WPF's <c>CommandManager</c> (the <see cref="RelayCommand"/> wires <c>RequerySuggested</c>).
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

    private OperationPlan? _pendingJunkPlan;
    private OperationPlan? _pendingStartupPlan;
    private bool _isBusy;
    private bool _junkScanned;
    private bool _recycleConfirmPending;
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

        ScanJunkCommand = new AsyncRelayCommand(ScanJunkAsync, () => !IsBusy);
        RunJunkCommand = new AsyncRelayCommand(RunJunkAsync, () => !IsBusy && _pendingJunkPlan is { IsEmpty: false });
        LoadStartupCommand = new AsyncRelayCommand(LoadStartupAsync, () => !IsBusy);
        DisableStartupCommand = new AsyncRelayCommand(DisableStartupAsync, () => !IsBusy && _selectedStartup is not null);
        RefreshRecycleCommand = new AsyncRelayCommand(RefreshRecycleAsync, () => !IsBusy);
        // Emptying the bin is irreversible → stage a confirm first; the actual empty needs explicit approval.
        EmptyRecycleCommand = new RelayCommand(StageEmptyRecycle, () => !IsBusy && !RecycleConfirmPending);
        ConfirmEmptyRecycleCommand = new AsyncRelayCommand(ConfirmEmptyRecycleAsync, () => RecycleConfirmPending && !IsBusy);
        CancelEmptyRecycleCommand = new RelayCommand(() => RecycleConfirmPending = false, () => RecycleConfirmPending);
        LoadExtensionsCommand = new AsyncRelayCommand(LoadExtensionsAsync, () => !IsBusy);
        OpenExtensionFolderCommand = new RelayCommand(p => OpenExtensionFolder(p as BrowserExtension), _ => !IsBusy);
    }

    public I18n I18n { get; }

    // Junk
    public ObservableCollection<PlanRow> JunkPreview { get; } = new();
    public ObservableCollection<PlanRow> JunkSkipped { get; } = new();
    public ICommand ScanJunkCommand { get; }
    public ICommand RunJunkCommand { get; }
    public bool JunkScanned { get => _junkScanned; private set => SetField(ref _junkScanned, value); }
    public bool JunkEmpty => _junkScanned && (_pendingJunkPlan?.IsEmpty ?? true);

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

    // Shared
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }

    public string ResultSummary
    {
        get => _resultSummary;
        private set
        {
            if (SetField(ref _resultSummary, value))
                OnPropertyChanged(nameof(HasResult));
        }
    }

    /// <summary>True once an execution/empty has produced a summary line, so the result banner can show.</summary>
    public bool HasResult => !string.IsNullOrEmpty(_resultSummary);

    // ---- Junk ----

    private async Task ScanJunkAsync()
    {
        IsBusy = true;
        JunkPreview.Clear();
        JunkSkipped.Clear();
        try
        {
            var scanner = new JunkScanner(_junkProbe, _gate);
            JunkScanResult result = await Task.Run(() => scanner.Scan(DateTime.UtcNow));
            _pendingJunkPlan = result.Plan;
            foreach (PlannedAction a in result.Plan.Actions)
                JunkPreview.Add(PlanRow.FromAction(a, I18n));
            foreach (SkippedAction s in result.Skipped)
                JunkSkipped.Add(PlanRow.FromSkipped(s.Action, s.Reason, I18n));
        }
        finally
        {
            JunkScanned = true;
            OnPropertyChanged(nameof(JunkEmpty));
            IsBusy = false;
        }
    }

    private async Task RunJunkAsync()
    {
        OperationPlan? plan = _pendingJunkPlan;
        if (plan is null || plan.IsEmpty)
            return;

        await RunPlanAsync(plan);
        // Re-scan so the preview reflects what is now gone.
        await ScanJunkAsync();
    }

    /// <summary>Localized caution note for a non-Complete inventory source; empty string when Complete
    /// (so the bound TextBlock stays collapsed via NonEmptyToVisibleConverter). Partial and Unavailable both
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

    /// <summary>Shows the confirm panel; nothing is emptied until the user approves it.</summary>
    private void StageEmptyRecycle() => RecycleConfirmPending = true;

    private async Task ConfirmEmptyRecycleAsync()
    {
        RecycleConfirmPending = false;
        var plan = new OperationPlan(
            "Empty Recycle Bin",
            "clean",
            new PlannedAction[]
            {
                new EmptyRecycleBinAction
                {
                    Description = "Empty the Recycle Bin on all drives",
                    Reason = "User approved irreversible Recycle Bin empty",
                },
            },
            DateTime.UtcNow);

        try
        {
            await RunPlanAsync(plan);
        }
        finally
        {
            await RefreshRecycleAsync();
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

    /// <summary>The one execution path: hash the previewed plan, run it through the executor, show the report.</summary>
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

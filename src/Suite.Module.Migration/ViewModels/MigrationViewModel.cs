using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Execution;
using WindowsCareKit.Core.Modules.Migration.Selection;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.App.ViewModels;

/// <summary>
/// Migration orchestration: completed detection → scan/profile gate → derived badges/defaults/groups →
/// selection → display-only command preview/manual checklist, plus a runner-backed dry-run → hash → explicit
/// approval → capture flow. The existing <see cref="IMigrationBackupRunner"/> is the only execution path.
/// </summary>
public sealed class MigrationViewModel : ObservableObject, IWckNavigationAware
{
    private readonly IMigrationScanService _scanService;
    private readonly IMigrationBackupRunner _backupRunner;
    private readonly Func<IReadOnlyList<MigrationRecipe>> _recipeSource;
    private CancellationTokenSource? _scanCancellation;
    private int _scanStarted;
    private ScanReadyGate? _scanGate;
    private FeasibilityCeilingText? _ceiling;
    private bool _isScanning;
    private string _scanError = string.Empty;
    private string _packageDir = string.Empty;
    private bool _isBusy;
    private bool _isPreviewApproved;
    private string? _approvedHash;
    private MigrationBackupPlanResult? _capturePlanResult;
    private string _captureSummary = string.Empty;
    private string _packageWarning = string.Empty;

    public MigrationViewModel(
        I18n i18n,
        IMigrationScanService scanService,
        IMigrationBackupRunner backupRunner,
        Func<IReadOnlyList<MigrationRecipe>> recipeSource)
    {
        I18n = i18n ?? throw new ArgumentNullException(nameof(i18n));
        _scanService = scanService ?? throw new ArgumentNullException(nameof(scanService));
        _backupRunner = backupRunner ?? throw new ArgumentNullException(nameof(backupRunner));
        _recipeSource = recipeSource ?? throw new ArgumentNullException(nameof(recipeSource));
        I18n.PropertyChanged += OnLanguageChanged;

        StartScanCommand = new AsyncRelayCommand(StartScanAsync, () => !IsScanning && !IsScanComplete);
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);
        ConfirmProfileCommand = new RelayCommand(ConfirmProfile, () => ScanGate is { EnumerationComplete: true, ProfileConfirmed: false });
        ToggleGroupCommand = new RelayCommand(
            parameter => ToggleGroup(parameter as MigrationGroupRow),
            _ => CanSelect);
        ToggleItemCommand = new RelayCommand(
            parameter => ToggleItem(parameter as MigrationItemRow),
            _ => CanSelect);
        SelectRecommendedCommand = new RelayCommand(SelectRecommended, () => CanSelect);
        ClearOptionalCommand = new RelayCommand(ClearOptional, () => CanSelect);
        PreviewCommandsCommand = new RelayCommand(BuildPreview, () => CanSelect && SelectedCount > 0);
        BuildCapturePlanCommand = new AsyncRelayCommand(
            BuildCapturePlanAsync,
            () => !IsScanning && !IsBusy && SelectedCount > 0 && HasPackageDir);
        RunCaptureCommand = new AsyncRelayCommand(
            RunCaptureAsync,
            () => CanRunCapture);
    }

    public ObservableCollection<MigrationGroupRow> Groups { get; } = new();
    public ObservableCollection<string> CommandPreview { get; } = new();
    public ObservableCollection<ManualTodoEntry> ManualTodo { get; } = new();
    public ObservableCollection<string> ManualTodoDisplay { get; } = new();
    public ObservableCollection<CategoryCoverage> Coverage { get; } = new();
    public ObservableCollection<MigrationSourceRow> SourceRows { get; } = new();
    public ObservableCollection<PlanRow> CapturePlanRows { get; } = new();
    public ObservableCollection<PlanRow> CaptureSkippedRows { get; } = new();
    public ObservableCollection<PlanRow> CaptureResultRows { get; } = new();

    public I18n I18n { get; }
    public ICommand StartScanCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand ConfirmProfileCommand { get; }
    public ICommand ToggleGroupCommand { get; }
    public ICommand ToggleItemCommand { get; }
    public ICommand SelectRecommendedCommand { get; }
    public ICommand ClearOptionalCommand { get; }
    public ICommand PreviewCommandsCommand { get; }
    public ICommand BuildCapturePlanCommand { get; }
    public ICommand RunCaptureCommand { get; }

    public ScanReadyGate? ScanGate
    {
        get => _scanGate;
        private set
        {
            if (SetField(ref _scanGate, value))
            {
                OnPropertyChanged(nameof(CanSelect));
                OnPropertyChanged(nameof(IsScanComplete));
                OnPropertyChanged(nameof(ProfileSummary));
            }
        }
    }

    public FeasibilityCeilingText? Ceiling
    {
        get => _ceiling;
        private set
        {
            if (SetField(ref _ceiling, value))
                OnPropertyChanged(nameof(CeilingText));
        }
    }

    public bool IsScanComplete => ScanGate?.EnumerationComplete == true;
    public bool CanSelect => ScanGate?.CanSelect == true && !IsBusy;
    /// <summary>A6: the footer counts app rows, not files (the dry-run plan is still file-level and says so).</summary>
    public int SelectedCount => Groups.Sum(group => group.SelectedAppCount);
    public bool HasCommandPreview => CommandPreview.Count > 0;
    public bool HasManualTodo => ManualTodo.Count > 0;
    public string SelectedSummary => I18n.Format("migration.footer.selected", SelectedCount);
    public string ProfileSummary => ScanGate is null
        ? I18n["migration.scan.profile.pending"]
        : I18n.Format("migration.scan.profile", ScanGate.ProfileName, ScanGate.ResolvedProfileRoot);
    public string CeilingText => I18n.Culture == "tr" ? Ceiling?.Tr ?? string.Empty : Ceiling?.En ?? string.Empty;
    public bool HasPackageDir => !string.IsNullOrWhiteSpace(_packageDir);
    public bool HasCapturePlan => _capturePlanResult is not null;
    public bool HasCaptureResults => CaptureResultRows.Count > 0;
    public bool CanRunCapture =>
        !IsScanning
        && !IsBusy
        && IsPreviewApproved
        && SelectedCount > 0
        && HasPackageDir
        && _capturePlanResult is { Plan.IsEmpty: false }
        && _approvedHash is not null;

    public string PackageDir
    {
        get => _packageDir;
        set
        {
            if (IsBusy)
                return;

            if (SetField(ref _packageDir, value))
            {
                OnPropertyChanged(nameof(HasPackageDir));
                ResetCapturePlan();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSelect));
                OnPropertyChanged(nameof(CanRunCapture));
                OnPropertyChanged(nameof(CanEditDirectories));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool CanEditDirectories => !IsBusy;

    public bool IsPreviewApproved
    {
        get => _isPreviewApproved;
        set
        {
            if (SetField(ref _isPreviewApproved, value))
            {
                OnPropertyChanged(nameof(CanRunCapture));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string CaptureSummary
    {
        get => _captureSummary;
        private set => SetField(ref _captureSummary, value);
    }

    public string PackageWarning
    {
        get => _packageWarning;
        private set => SetField(ref _packageWarning, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(CanRunCapture));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string ScanError
    {
        get => _scanError;
        private set => SetField(ref _scanError, value);
    }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken) => StartScanAsync(cancellationToken);

    /// <summary>Runs the read-only scan once, off the UI thread, then applies the result on the caller's UI context.</summary>
    public async Task StartScanAsync(CancellationToken cancellationToken = default)
    {
        if (IsScanComplete || Interlocked.CompareExchange(ref _scanStarted, 1, 0) != 0)
            return;

        SynchronizationContext? uiContext = SynchronizationContext.Current;
        CancellationTokenSource scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _scanCancellation = scanCancellation;
        CancellationToken token = scanCancellation.Token;
        IsScanning = true;
        ScanError = string.Empty;
        bool allowRetry = false;

        try
        {
            MigrationScanResult result = await Task.Run(() => _scanService.Scan(token), token).ConfigureAwait(false);
            await RunOnContextAsync(uiContext, () =>
            {
                // Re-check immediately before applying: the shell may have cancelled navigation to this view
                // (or a user Cancel arrived) after the background scan returned but before this posted callback
                // ran. Applying scan state to a now-deactivated view would be a stale mutation (P27-R2); throwing
                // here routes through the existing OperationCanceledException handling below, which resets
                // _scanStarted so a later navigation retries instead of latching a phantom "complete".
                token.ThrowIfCancellationRequested();
                LoadScan(result.Detection, result.ProfileRoot, result.Candidates);
                ConfirmProfile();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            allowRetry = true;
        }
        catch
        {
            allowRetry = true;
            await RunOnContextAsync(uiContext, () => ScanError = I18n["migration.scan.error"]).ConfigureAwait(false);
        }
        finally
        {
            await RunOnContextAsync(uiContext, () => IsScanning = false).ConfigureAwait(false);
            scanCancellation.Dispose();
            if (ReferenceEquals(_scanCancellation, scanCancellation))
                _scanCancellation = null;
            if (allowRetry)
                Interlocked.Exchange(ref _scanStarted, 0);
        }
    }

    public void CancelScan() => _scanCancellation?.Cancel();

    /// <summary>
    /// Builds the dry-run migration capture plan from the distinct recipes represented by the current
    /// selection. The runner remains the sole source of per-file actions and honest skips.
    /// </summary>
    public async Task BuildCapturePlanAsync()
    {
        if (IsScanning || IsBusy || SelectedCount == 0 || !HasPackageDir)
            return;

        IsBusy = true;
        ResetCapturePlan();
        try
        {
            if (!TryNormalizePackageDir(_packageDir, out string packageDir))
            {
                PackageWarning = I18n["migration.capture.outsideAppWarning"];
                return;
            }

            string[] selectedRecipeIds = SelectedRecipeIds().ToArray();

            (MigrationBackupPlanResult Result, HashSet<string> WholeTreeIds) built = await Task.Run(() =>
            {
                HashSet<string> selectedIds = selectedRecipeIds.ToHashSet(StringComparer.Ordinal);
                MigrationRecipe[] selectedRecipes = _recipeSource()
                    .Where(recipe => selectedIds.Contains(MigrationAppIdentity.Canonicalize(recipe.Id)))
                    .ToArray();
                MigrationBackupPlanResult result =
                    _backupRunner.BuildPlan(selectedRecipes, packageDir, DateTime.UtcNow);

                var wholeTreeIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (PlannedAction action in result.Plan.Actions)
                    if (action is CopyAction copy
                        && !string.IsNullOrWhiteSpace(copy.Source)
                        && Directory.Exists(copy.Source))
                        wholeTreeIds.Add(copy.Id);
                return (result, wholeTreeIds);
            });

            string[] currentRecipeIds = SelectedRecipeIds().ToArray();
            if (!TryNormalizePackageDir(_packageDir, out string currentPackageDir)
                || !string.Equals(currentPackageDir, packageDir, StringComparison.OrdinalIgnoreCase)
                || !selectedRecipeIds.SequenceEqual(currentRecipeIds, StringComparer.OrdinalIgnoreCase))
                return;

            _capturePlanResult = built.Result;
            _approvedHash = built.Result.Plan.ComputeHash();
            if (!string.Equals(_packageDir, packageDir, StringComparison.Ordinal))
            {
                _packageDir = packageDir;
                OnPropertyChanged(nameof(PackageDir));
            }

            foreach (PlannedAction action in built.Result.Plan.Actions)
                CapturePlanRows.Add(PlanRow.FromAction(action, built.WholeTreeIds.Contains(action.Id)));
            foreach (RecipeItemSkip skip in built.Result.SkippedItems)
                CaptureSkippedRows.Add(SkipRow(skip));

            CaptureSummary = I18n.Format(
                "migration.capture.previewSummary",
                built.Result.Plan.Actions.Count,
                built.Result.SkippedItems.Count);
            OnPropertyChanged(nameof(HasCapturePlan));
            OnPropertyChanged(nameof(CanRunCapture));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Executes only the approved, hashed dry-run plan through <see cref="IMigrationBackupRunner"/>.</summary>
    public async Task RunCaptureAsync()
    {
        if (!CanRunCapture || _capturePlanResult is null || _approvedHash is null)
            return;

        IsBusy = true;
        CaptureResultRows.Clear();
        try
        {
            MigrationBackupPlanResult plan = _capturePlanResult;
            string approvedHash = _approvedHash;
            string packageDir = _packageDir;
            MigrationBackupRunResult result =
                await Task.Run(() => _backupRunner.Run(plan, approvedHash, packageDir));

            foreach (CopyFileOutcome outcome in result.CopyReport.Outcomes)
                CaptureResultRows.Add(ResultRow(outcome));
            foreach (RecipeItemSkip skip in result.FinalizationSkips)
                CaptureResultRows.Add(SkipRow(skip));

            CaptureSummary = result.Authorized
                ? I18n.Format(
                    "migration.capture.resultSummary",
                    result.CopyReport.Copied.Count,
                    result.CopyReport.Skipped.Count + result.FinalizationSkips.Count)
                : RefusedSummary(result.CopyReport);
            OnPropertyChanged(nameof(HasCaptureResults));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Load a completed, read-only scan. Selection remains disabled until profile confirmation.</summary>
    public void LoadScan(
        DetectionResult detection,
        string resolvedProfileRoot,
        IEnumerable<MigrationSelectionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(candidates);

        Groups.Clear();
        IReadOnlyList<MigrationSelectionGroup> models = MigrationSelectionBuilder.Build(candidates);
        foreach (MigrationSelectionGroup model in models)
            Groups.Add(new MigrationGroupRow(model, SelectionChanged, I18n));

        Coverage.Clear();
        foreach (CategoryCoverage coverage in MigrationCoverageCalculator.ByCategory(models))
            Coverage.Add(coverage);

        CommandPreview.Clear();
        ManualTodo.Clear();
        ManualTodoDisplay.Clear();
        ResetCapturePlan();
        Ceiling = MigrationCoverageCalculator.BuildBanner(detection);
        ScanGate = ScanReadyGate.Complete(detection, resolvedProfileRoot);
        SourceRows.Clear();
        foreach (ProgramSourceReport report in detection.SourceReports)
            SourceRows.Add(new MigrationSourceRow(report, I18n));
        RaiseDerivedState();
    }

    private void ConfirmProfile()
    {
        if (ScanGate is not { EnumerationComplete: true } gate)
            return;
        ScanGate = gate.ConfirmProfile();
        RaiseDerivedState();
    }

    private void ToggleGroup(MigrationGroupRow? group)
    {
        if (!CanSelect || group is null)
            return;
        group.SetAll(group.IsChecked != true);
    }

    private void ToggleItem(MigrationItemRow? item)
    {
        if (!CanSelect || item is null)
            return;
        item.IsSelected = !item.IsSelected;
    }

    /// <summary>A3: "Select recommended" operates on apps — an app's recommended state is ON if ANY part's
    /// smart default recommends selection, and that decision cascades to every part.</summary>
    private void SelectRecommended()
    {
        foreach (MigrationGroupRow group in Groups)
            foreach (MigrationAppRow app in group.Apps)
                app.ResetToRecommended();
    }

    private void ClearOptional()
    {
        foreach (MigrationGroupRow group in Groups)
            group.SetAll(false);
    }

    private void BuildPreview()
    {
        if (!CanSelect)
            return;

        MigrationSelectionItem[] selected = SelectedItems()
            .Select(item => item.Model)
            .ToArray();

        CommandPreview.Clear();
        foreach (string command in MigrationCommandPreviewGenerator.GenerateSelected(selected))
            CommandPreview.Add(command);

        ManualTodo.Clear();
        ManualTodoDisplay.Clear();
        var seen = new HashSet<(string Code, string Tr, string En)>();
        foreach (ManualTodoEntry todo in selected.SelectMany(ManualTodoRenderer.Render))
            if (seen.Add((todo.Code, todo.Tr, todo.En)))
            {
                ManualTodo.Add(todo);
                ManualTodoDisplay.Add(I18n.Culture == "tr" ? todo.Tr : todo.En);
            }

        OnPropertyChanged(nameof(HasCommandPreview));
        OnPropertyChanged(nameof(HasManualTodo));
    }

    private void SelectionChanged()
    {
        CommandPreview.Clear();
        ManualTodo.Clear();
        ManualTodoDisplay.Clear();
        ResetCapturePlan();
        RaiseDerivedState();
    }

    private void RaiseDerivedState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasCommandPreview));
        OnPropertyChanged(nameof(HasManualTodo));
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(ProfileSummary));
        OnPropertyChanged(nameof(CeilingText));
        OnPropertyChanged(nameof(CanRunCapture));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(ProfileSummary));
        OnPropertyChanged(nameof(CeilingText));
        foreach (MigrationGroupRow group in Groups)
            group.RefreshLanguage();
        foreach (MigrationSourceRow source in SourceRows)
            source.RefreshLanguage();
        RefreshManualTodoLanguage();
    }

    private IEnumerable<MigrationAppRow> SelectedApps()
        => Groups.SelectMany(group => group.Apps).Where(app => app.IsChecked);

    private IEnumerable<MigrationItemRow> SelectedItems()
        => SelectedApps().SelectMany(app => app.Parts);

    private IEnumerable<string> SelectedRecipeIds()
        => SelectedApps().Select(app => app.RecipeId);

    private void ResetCapturePlan()
    {
        CapturePlanRows.Clear();
        CaptureSkippedRows.Clear();
        CaptureResultRows.Clear();
        _capturePlanResult = null;
        _approvedHash = null;
        IsPreviewApproved = false;
        CaptureSummary = string.Empty;
        PackageWarning = string.Empty;
        OnPropertyChanged(nameof(HasCapturePlan));
        OnPropertyChanged(nameof(HasCaptureResults));
        OnPropertyChanged(nameof(CanRunCapture));
        CommandManager.InvalidateRequerySuggested();
    }

    private static bool TryNormalizePackageDir(string packageDir, out string normalized)
    {
        normalized = string.Empty;
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDir));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (full.Length < 2 || !char.IsLetter(full[0]) || full[1] != ':')
            return false;

        string appDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        if (string.Equals(full, appDir, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(appDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        normalized = full;
        return true;
    }

    private string RefusedSummary(CopySkipReport report)
    {
        string generic = I18n["migration.capture.refused"];
        string? reason = report.Skipped.FirstOrDefault()?.Detail;
        return string.IsNullOrWhiteSpace(reason) ? generic : $"{generic}: {reason}";
    }

    private static PlanRow SkipRow(RecipeItemSkip skip) => new()
    {
        Text = skip.ItemPath,
        RiskText = "SKIPPED",
        RiskBrush = RiskVisuals.For(RiskLevel.Info),
        Undo = string.Empty,
        Detail = skip.Reason,
    };

    private static PlanRow ResultRow(CopyFileOutcome outcome) => new()
    {
        Text = outcome.Source,
        RiskText = outcome.Copied ? "COPIED" : "SKIPPED",
        RiskBrush = RiskVisuals.For(outcome.Copied ? RiskLevel.Low : RiskLevel.Critical),
        Undo = string.Empty,
        Detail = outcome.Copied ? outcome.Destination : $"{outcome.Reason}: {outcome.Detail}",
    };

    private void RefreshManualTodoLanguage()
    {
        ManualTodoDisplay.Clear();
        foreach (ManualTodoEntry todo in ManualTodo)
            ManualTodoDisplay.Add(I18n.Culture == "tr" ? todo.Tr : todo.En);
    }

    private static Task RunOnContextAsync(SynchronizationContext? context, Action action)
    {
        if (context is null || ReferenceEquals(context, SynchronizationContext.Current))
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }, null);
        return completion.Task;
    }
}

/// <summary>Binding row over the pure group model. Its nullable checkbox aggregates and toggles visible
/// application rows; file-level parts are read-only presentation detail in v1.</summary>
public sealed class MigrationGroupRow : ObservableObject
{
    private readonly MigrationSelectionGroup _model;
    private readonly Action _selectionChanged;
    private readonly I18n _i18n;

    internal MigrationGroupRow(
        MigrationSelectionGroup model,
        Action selectionChanged,
        I18n i18n)
    {
        _model = model;
        _selectionChanged = selectionChanged;
        _i18n = i18n;

        var itemRows = new Dictionary<MigrationSelectionItem, MigrationItemRow>();
        foreach (MigrationSelectionItem item in model.Items)
            itemRows[item] = new MigrationItemRow(item, ItemChanged, i18n);
        Items = new ObservableCollection<MigrationItemRow>(model.Items.Select(item => itemRows[item]));
        Apps = new ObservableCollection<MigrationAppRow>(model.Apps.Select(app => new MigrationAppRow(
            app, app.Parts.Select(part => itemRows[part]).ToArray(), AppChanged, i18n)));
    }

    public MigrationCategory Category => _model.Category;
    public string Title => _i18n[$"migration.group.{Category}.title"];
    public string Subtitle => _i18n[$"migration.group.{Category}.subtitle"];

    /// <summary>A6: the count the user sees is app rows, not files.</summary>
    public string CountSummary => _i18n.Format("migration.group.count", Apps.Count, SelectedAppCount);
    public ObservableCollection<MigrationItemRow> Items { get; }
    public ObservableCollection<MigrationAppRow> Apps { get; }
    public int SelectedCount => _model.SelectedCount;
    public int SelectedAppCount => _model.SelectedAppCount;
    public GroupSelectionState SelectionState => _model.SelectionState;

    public bool? IsChecked
    {
        get => SelectionState switch
        {
            GroupSelectionState.All => true,
            GroupSelectionState.None => false,
            _ => null,
        };
        set
        {
            if (value.HasValue)
                SetAll(value.Value);
        }
    }

    public void SetAll(bool selected)
    {
        _model.SetAll(selected);
        Refresh();
    }

    private void ItemChanged()
    {
        foreach (MigrationItemRow item in Items)
            item.Refresh();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionState));
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(SelectedAppCount));
        OnPropertyChanged(nameof(CountSummary));
        foreach (MigrationAppRow app in Apps)
            app.Refresh();
        _selectionChanged();
    }

    private void Refresh()
    {
        foreach (MigrationItemRow item in Items)
            item.Refresh();
        foreach (MigrationAppRow app in Apps)
            app.Refresh();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionState));
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(SelectedAppCount));
        OnPropertyChanged(nameof(CountSummary));
        _selectionChanged();
    }

    private void AppChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionState));
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(SelectedAppCount));
        OnPropertyChanged(nameof(CountSummary));
        _selectionChanged();
    }

    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(CountSummary));
        foreach (MigrationItemRow item in Items)
            item.RefreshLanguage();
        foreach (MigrationAppRow app in Apps)
            app.RefreshLanguage();
    }

}

/// <summary>
/// Binding row over one app (A2/A4): every part sharing a recipe id. The checkbox is the ONLY selection
/// control in v1 (A3). The model normalizes defaults and forced apps too, so the defensive ANY getter can never
/// expose a partial recipe while capture operates at application granularity.
/// </summary>
public sealed class MigrationAppRow : ObservableObject
{
    private readonly MigrationAppGroup _model;
    private readonly Action _selectionChanged;
    private readonly I18n _i18n;
    private bool _isExpanded;

    internal MigrationAppRow(
        MigrationAppGroup model, IReadOnlyList<MigrationItemRow> parts, Action selectionChanged, I18n i18n)
    {
        _model = model;
        Parts = parts;
        _selectionChanged = selectionChanged;
        _i18n = i18n;
    }

    public IReadOnlyList<MigrationItemRow> Parts { get; }
    public string RecipeId => _model.RecipeId;
    public MigrationSelectionCandidate Representative => _model.Representative;
    public string Title => Representative.DisplayName;
    public string Subtitle
    {
        get
        {
            string? localized = _i18n.Culture == "tr" ? Representative.WhatHappensTr : Representative.WhatHappensEn;
            if (!string.IsNullOrWhiteSpace(localized))
                return localized;
            return string.IsNullOrWhiteSpace(Representative.WhatHappens)
                ? _i18n["migration.item.defaultDescription"]
                : Representative.WhatHappens;
        }
    }
    public bool HasMultipleParts => _model.HasMultipleParts;
    public string PartsSummary => _i18n.Format("migration.app.parts", Parts.Count);
    public bool IsForced => _model.IsForced;
    public string? ForcedTooltip => IsForced ? _i18n["migration.item.forcedTooltip"] : null;
    public bool HasSecretOverlay => Badge.HasSecretOverlay;
    public string? SecretExcludedText => HasSecretOverlay ? _i18n["migration.app.secretExcluded"] : null;

    /// <summary>Neutral source summary: one literal path or a localized count when roots differ.</summary>
    public string? SourceSummary
    {
        get
        {
            string[] sources = Parts
                .Select(part => part.Candidate.SourcePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return sources.Length switch
            {
                0 => null,
                1 => sources[0],
                _ => _i18n.Format("migration.app.multipleSources", sources.Length),
            };
        }
    }

    public long? TotalSizeBytes
    {
        get
        {
            if (Parts.Count == 0 || Parts.Any(part => !part.Candidate.SizeBytes.HasValue))
                return null;
            return Parts.Sum(part => part.Candidate.SizeBytes!.Value);
        }
    }

    public string? SizeText => TotalSizeBytes is { } bytes ? MigrationByteSizeFormatter.Format(bytes) : null;

    /// <summary>A5: the worst-of aggregation, a pure function over the parts' own (unmodified) badges.</summary>
    public MigrationBadgePresentation Badge => MigrationBadgeAggregator.Aggregate(PartBadges());

    public string BadgeText => $"{Badge.Glyph} {(_i18n.Culture == "tr" ? Badge.LabelTr : Badge.LabelEn)}";

    /// <summary>A5: "3/4 taşınabilir" — only rendered when parts genuinely differ.</summary>
    public string? BreakdownText
    {
        get
        {
            if (MigrationBadgeAggregator.Breakdown(PartBadges()) is not { } breakdown)
                return null;
            string label = _i18n.Culture == "tr" ? breakdown.LabelTr : breakdown.LabelEn;
            return _i18n.Format("migration.app.badgeBreakdown", breakdown.BestCount, breakdown.Total, label);
        }
    }

    /// <summary>A4 meta line: neutral source summary · total size when fully known · badge breakdown.</summary>
    public string MetaLine
    {
        get
        {
            var segments = new List<string>();
            if (!string.IsNullOrWhiteSpace(SourceSummary))
                segments.Add(SourceSummary!);
            if (SizeText is not null)
                segments.Add(SizeText);
            if (BreakdownText is not null)
                segments.Add(BreakdownText);
            return string.Join(" · ", segments);
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
                OnPropertyChanged(nameof(ExpansionGlyph));
        }
    }

    public string ExpansionGlyph => IsExpanded ? "⌃" : "⌄";

    public bool IsChecked
    {
        get => IsForced || Parts.Any(part => part.IsSelected);
        set
        {
            if (IsForced)
                return;
            _model.SetAppSelected(value);
            foreach (MigrationItemRow part in Parts)
                part.Refresh();
            Refresh();
            _selectionChanged();
        }
    }

    /// <summary>A3: "Select recommended" cascades the app-level OR-aggregate default to every part.</summary>
    internal void ResetToRecommended()
    {
        _model.SetAppSelected(_model.SmartDefaultOn);
        foreach (MigrationItemRow part in Parts)
            part.Refresh();
        Refresh();
        _selectionChanged();
    }

    /// <summary>Re-raise property-changed for this row's derived bits after a part changed through some other
    /// path (category toggle, direct item toggle). Pure notification — no mutation, safe to call repeatedly.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(Badge));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(BreakdownText));
        OnPropertyChanged(nameof(HasSecretOverlay));
        OnPropertyChanged(nameof(SecretExcludedText));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(MetaLine));
    }

    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(PartsSummary));
        OnPropertyChanged(nameof(ForcedTooltip));
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(BreakdownText));
        OnPropertyChanged(nameof(SecretExcludedText));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(MetaLine));
    }

    private MigrationBadgePresentation[] PartBadges() => Parts.Select(part => part.Badge).ToArray();
}

/// <summary>Binding row over one pure selection item.</summary>
public sealed class MigrationItemRow : ObservableObject
{
    private readonly Action _selectionChanged;
    private readonly I18n _i18n;

    internal MigrationItemRow(
        MigrationSelectionItem model, Action selectionChanged, I18n i18n)
    {
        Model = model;
        _selectionChanged = selectionChanged;
        _i18n = i18n;
    }

    internal MigrationSelectionItem Model { get; }
    public MigrationSelectionCandidate Candidate => Model.Candidate;
    public MigrationBadgePresentation Badge => Model.Badge;
    public SmartDefaultDecision SmartDefault => Model.SmartDefault;
    public bool IsForcedSelected => Model.IsForcedSelected;
    public string? ForcedSelectionToolTip => IsForcedSelected ? _i18n["migration.item.forcedTooltip"] : null;
    public string BadgeText => $"{Badge.Glyph} {(_i18n.Culture == "tr" ? Badge.LabelTr : Badge.LabelEn)}";

    /// <summary>A4 part label: the recipe's declared v4 <c>label</c> for this part, or the path leaf when
    /// none was declared (v1-v3 recipes, or a part the recipe author didn't label).</summary>
    public string PartLabel
    {
        get
        {
            string? localized = _i18n.Culture == "tr" ? Candidate.Meta.PartLabel?.Tr : Candidate.Meta.PartLabel?.En;
            return string.IsNullOrWhiteSpace(localized) ? PathLeafFallback() : localized;
        }
    }

    /// <summary>A4 part row: size, when known (omitted otherwise — never a fabricated "0 B").</summary>
    public string? SizeText => Candidate.SizeBytes is { } bytes ? MigrationByteSizeFormatter.Format(bytes) : null;

    /// <summary>A4: "Locked-now reasons ... render here, on the offending part" — true when this specific
    /// part (not necessarily its siblings) is the one the content probe found in use right now.</summary>
    public bool HasLockedNowReason => Candidate.Meta.ContentProbeStatus == ContentProbeStatus.LockedNow;

    public string WhatHappens
    {
        get
        {
            if (Candidate.Meta.ContentProbeStatus == ContentProbeStatus.LockedNow)
                return LockedNowReason();

            string? localized = _i18n.Culture == "tr" ? Candidate.WhatHappensTr : Candidate.WhatHappensEn;
            if (!string.IsNullOrWhiteSpace(localized))
                return localized;
            return string.IsNullOrWhiteSpace(Candidate.WhatHappens)
                ? _i18n["migration.item.defaultDescription"]
                : Candidate.WhatHappens;
        }
    }

    public bool IsSelected
    {
        get => Model.IsSelected;
        set
        {
            bool before = Model.IsSelected;
            Model.SetSelected(value);
            if (before != Model.IsSelected)
            {
                OnPropertyChanged();
                _selectionChanged();
            }
            else if (value != Model.IsSelected)
            {
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(WhatHappens));
        OnPropertyChanged(nameof(ForcedSelectionToolTip));
        OnPropertyChanged(nameof(PartLabel));
    }

    /// <summary>A4 fallback: the final path segment (e.g. <c>CLAUDE.md</c>, <c>projects</c>), separator-agnostic.</summary>
    private string PathLeafFallback()
    {
        string? path = Candidate.SourcePath ?? Candidate.DestinationPath;
        if (string.IsNullOrWhiteSpace(path))
            return Candidate.DisplayName;
        string normalized = path.Replace('\\', '/').TrimEnd('/');
        int slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private string LockedNowReason()
    {
        string? process = Candidate.Meta.Preconditions
            .Select(ProcessNameFromPrecondition)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

        return string.IsNullOrWhiteSpace(process)
            ? _i18n["migration.item.reason.lockedNow.generic"]
            : _i18n.Format("migration.item.reason.lockedNow", process);
    }

    private static string? ProcessNameFromPrecondition(string precondition)
    {
        const string Prefix = "process-closed:";
        if (!precondition.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string process = precondition[Prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(process) ? null : process;
    }
}

public sealed class MigrationSourceRow : ObservableObject
{
    private readonly ProgramSourceReport _report;
    private readonly I18n _i18n;

    internal MigrationSourceRow(ProgramSourceReport report, I18n i18n)
    {
        _report = report;
        _i18n = i18n;
    }

    public string Glyph => _report.Status == ProgramSourceStatus.Ok ? "✅" : "⚠️";
    public string Text => _i18n.Format(
        $"migration.scan.source.{_report.Status}",
        _i18n[$"migration.scan.source.{_report.Kind}"],
        _report.Count);
    public string Detail => _report.Detail ?? string.Empty;

    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Detail));
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Execution;
using WindowsCareKit.Core.Modules.Migration.Selection;

namespace WindowsCareKit.App.ViewModels;

/// <summary>
/// Headless M3 orchestration: completed detection → scan/profile gate → derived badges/defaults/groups →
/// selection → display-only command preview/manual checklist. It has no View, navigation, IO, or executor.
/// </summary>
public sealed class MigrationViewModel : ObservableObject
{
    private readonly IMigrationScanService _scanService;
    private CancellationTokenSource? _scanCancellation;
    private int _scanStarted;
    private ScanReadyGate? _scanGate;
    private FeasibilityCeilingText? _ceiling;
    private bool _isScanning;
    private string _scanError = string.Empty;

    public MigrationViewModel(I18n i18n, IMigrationScanService scanService)
    {
        I18n = i18n ?? throw new ArgumentNullException(nameof(i18n));
        _scanService = scanService ?? throw new ArgumentNullException(nameof(scanService));
        I18n.PropertyChanged += OnLanguageChanged;

        StartScanCommand = new RelayCommand(async () => await StartScanAsync(), () => !IsScanning && !IsScanComplete);
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
    }

    public ObservableCollection<MigrationGroupRow> Groups { get; } = new();
    public ObservableCollection<string> CommandPreview { get; } = new();
    public ObservableCollection<ManualTodoEntry> ManualTodo { get; } = new();
    public ObservableCollection<string> ManualTodoDisplay { get; } = new();
    public ObservableCollection<CategoryCoverage> Coverage { get; } = new();
    public ObservableCollection<MigrationSourceRow> SourceRows { get; } = new();

    public I18n I18n { get; }
    public ICommand StartScanCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand ConfirmProfileCommand { get; }
    public ICommand ToggleGroupCommand { get; }
    public ICommand ToggleItemCommand { get; }
    public ICommand SelectRecommendedCommand { get; }
    public ICommand ClearOptionalCommand { get; }
    public ICommand PreviewCommandsCommand { get; }

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
    public bool CanSelect => ScanGate?.CanSelect == true;
    public bool CanRestoreExecute => false;
    public string RestoreDisabledHelper => I18n["migration.restore.disabledHelper"];
    public int SelectedCount => Groups.Sum(group => group.SelectedCount);
    public bool HasCommandPreview => CommandPreview.Count > 0;
    public bool HasManualTodo => ManualTodo.Count > 0;
    public string SelectedSummary => I18n.Format("migration.footer.selected", SelectedCount);
    public string ProfileSummary => ScanGate is null
        ? I18n["migration.scan.profile.pending"]
        : I18n.Format("migration.scan.profile", ScanGate.ProfileName, ScanGate.ResolvedProfileRoot);
    public string CeilingText => I18n.Culture == "tr" ? Ceiling?.Tr ?? string.Empty : Ceiling?.En ?? string.Empty;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string ScanError
    {
        get => _scanError;
        private set => SetField(ref _scanError, value);
    }

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

    private void SelectRecommended()
    {
        foreach (MigrationGroupRow group in Groups)
            group.ResetToRecommended();
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

        MigrationSelectionItem[] selected = Groups
            .SelectMany(group => group.Items)
            .Where(item => item.IsSelected)
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
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(RestoreDisabledHelper));
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(ProfileSummary));
        OnPropertyChanged(nameof(CeilingText));
        foreach (MigrationGroupRow group in Groups)
            group.RefreshLanguage();
        foreach (MigrationSourceRow source in SourceRows)
            source.RefreshLanguage();
        RefreshManualTodoLanguage();
    }

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

/// <summary>Binding row over the pure group model. Its nullable checkbox is the three-state group header.</summary>
public sealed class MigrationGroupRow : ObservableObject
{
    private readonly MigrationSelectionGroup _model;
    private readonly Action _selectionChanged;
    private readonly I18n _i18n;

    internal MigrationGroupRow(MigrationSelectionGroup model, Action selectionChanged, I18n i18n)
    {
        _model = model;
        _selectionChanged = selectionChanged;
        _i18n = i18n;
        Items = new ObservableCollection<MigrationItemRow>(
            model.Items.Select(item => new MigrationItemRow(item, ItemChanged, i18n)));
    }

    public MigrationCategory Category => _model.Category;
    public string Title => _i18n[$"migration.group.{Category}.title"];
    public string Subtitle => _i18n[$"migration.group.{Category}.subtitle"];
    public string CountSummary => _i18n.Format("migration.group.count", Items.Count, SelectedCount);
    public ObservableCollection<MigrationItemRow> Items { get; }
    public int SelectedCount => _model.SelectedCount;
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

    internal void ResetToRecommended()
    {
        foreach (MigrationItemRow item in Items)
            item.Model.SetSelected(item.Model.SmartDefault.Kind is SmartDefaultKind.On or SmartDefaultKind.ForcedOnCritical);
        Refresh();
    }

    private void ItemChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionState));
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(CountSummary));
        _selectionChanged();
    }

    private void Refresh()
    {
        foreach (MigrationItemRow item in Items)
            item.Refresh();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionState));
        OnPropertyChanged(nameof(IsChecked));
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
    }
}

/// <summary>Binding row over one pure selection item.</summary>
public sealed class MigrationItemRow : ObservableObject
{
    private readonly Action _selectionChanged;
    private readonly I18n _i18n;

    internal MigrationItemRow(MigrationSelectionItem model, Action selectionChanged, I18n i18n)
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
    public string BadgeText => $"{Badge.Glyph} {(_i18n.Culture == "tr" ? Badge.LabelTr : Badge.LabelEn)}";
    public string WhatHappens
    {
        get
        {
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
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(WhatHappens));
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

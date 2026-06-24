using System.Collections.ObjectModel;
using System.Windows.Input;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Selection;

namespace WindowsCareKit.App.ViewModels;

/// <summary>
/// Headless M3 orchestration: completed detection → scan/profile gate → derived badges/defaults/groups →
/// selection → display-only command preview/manual checklist. It has no View, navigation, IO, or executor.
/// </summary>
public sealed class MigrationViewModel : ObservableObject
{
    private ScanReadyGate? _scanGate;
    private FeasibilityCeilingText? _ceiling;

    public MigrationViewModel()
    {
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
    public ObservableCollection<CategoryCoverage> Coverage { get; } = new();

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
            }
        }
    }

    public FeasibilityCeilingText? Ceiling
    {
        get => _ceiling;
        private set => SetField(ref _ceiling, value);
    }

    public bool IsScanComplete => ScanGate?.EnumerationComplete == true;
    public bool CanSelect => ScanGate?.CanSelect == true;
    public int SelectedCount => Groups.Sum(group => group.SelectedCount);
    public bool HasCommandPreview => CommandPreview.Count > 0;
    public bool HasManualTodo => ManualTodo.Count > 0;

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
            Groups.Add(new MigrationGroupRow(model, SelectionChanged));

        Coverage.Clear();
        foreach (CategoryCoverage coverage in MigrationCoverageCalculator.ByCategory(models))
            Coverage.Add(coverage);

        CommandPreview.Clear();
        ManualTodo.Clear();
        Ceiling = MigrationCoverageCalculator.BuildBanner(detection);
        ScanGate = ScanReadyGate.Complete(detection, resolvedProfileRoot);
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
        var seen = new HashSet<(string Code, string Tr, string En)>();
        foreach (ManualTodoEntry todo in selected.SelectMany(ManualTodoRenderer.Render))
            if (seen.Add((todo.Code, todo.Tr, todo.En)))
                ManualTodo.Add(todo);

        OnPropertyChanged(nameof(HasCommandPreview));
        OnPropertyChanged(nameof(HasManualTodo));
    }

    private void SelectionChanged()
    {
        CommandPreview.Clear();
        ManualTodo.Clear();
        RaiseDerivedState();
    }

    private void RaiseDerivedState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasCommandPreview));
        OnPropertyChanged(nameof(HasManualTodo));
        OnPropertyChanged(nameof(CanSelect));
    }
}

/// <summary>Binding row over the pure group model. Its nullable checkbox is the three-state group header.</summary>
public sealed class MigrationGroupRow : ObservableObject
{
    private readonly MigrationSelectionGroup _model;
    private readonly Action _selectionChanged;

    internal MigrationGroupRow(MigrationSelectionGroup model, Action selectionChanged)
    {
        _model = model;
        _selectionChanged = selectionChanged;
        Items = new ObservableCollection<MigrationItemRow>(
            model.Items.Select(item => new MigrationItemRow(item, ItemChanged)));
    }

    public MigrationCategory Category => _model.Category;
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
        _selectionChanged();
    }

    private void Refresh()
    {
        foreach (MigrationItemRow item in Items)
            item.Refresh();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionState));
        OnPropertyChanged(nameof(IsChecked));
        _selectionChanged();
    }
}

/// <summary>Binding row over one pure selection item.</summary>
public sealed class MigrationItemRow : ObservableObject
{
    private readonly Action _selectionChanged;

    internal MigrationItemRow(MigrationSelectionItem model, Action selectionChanged)
    {
        Model = model;
        _selectionChanged = selectionChanged;
    }

    internal MigrationSelectionItem Model { get; }
    public MigrationSelectionCandidate Candidate => Model.Candidate;
    public MigrationBadgePresentation Badge => Model.Badge;
    public SmartDefaultDecision SmartDefault => Model.SmartDefault;
    public bool IsForcedSelected => Model.IsForcedSelected;

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
}

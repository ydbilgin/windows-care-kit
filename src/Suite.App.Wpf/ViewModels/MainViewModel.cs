using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using WindowsCareKit.App.Mvvm;

namespace WindowsCareKit.App.ViewModels;

/// <summary>The shell: the navigation rail, the selected content, and the language selector.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<string, IWckModule> _modulesById;
    private readonly Dictionary<string, FrameworkElement> _moduleViews = new(StringComparer.OrdinalIgnoreCase);
    private NavItem _selectedNav = null!;

    public MainViewModel(I18n i18n, IReadOnlyList<IWckModule> modules)
        : this(i18n, modules, EmptyServiceProvider.Instance)
    {
    }

    internal MainViewModel(I18n i18n, IReadOnlyList<IWckModule> modules, IServiceProvider services)
    {
        I18n = i18n;
        _modulesById = modules.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        // Glyphs are Segoe MDL2 Assets / Segoe Fluent Icons code points (delete / clean / save / migrate / restore / download / gear).
        Nav = new ObservableCollection<NavItem>(
            modules
                .OrderBy(m => m.IsSettings ? 1 : 0)
                .ThenBy(m => m.Order)
                .Select(m => new NavItem(
                    i18n,
                    m.Id,
                    m.TitleKey,
                    m.IconKey,
                    m.CreateContent(services),
                    m.DescKey,
                    m.IsSettings)));

        DismissFirstRunCommand = new RelayCommand(() => ShowFirstRun = false);
        if (Nav.Count > 0)
            SelectedNav = Nav[0];
    }

    public I18n I18n { get; }
    public ObservableCollection<NavItem> Nav { get; }
    public ICommand DismissFirstRunCommand { get; }

    private bool _showFirstRun = true;
    public bool ShowFirstRun { get => _showFirstRun; set => SetField(ref _showFirstRun, value); }

    public NavItem SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (SetField(ref _selectedNav, value))
            {
                OnPropertyChanged(nameof(CurrentContent));
                if (value.Content is IWckNavigationAware aware)
                    aware.OnNavigatedTo();
            }
        }
    }

    public object CurrentContent => HostContent(_selectedNav);

    /// <summary>
    /// Opens a module by its short key (e.g. "migration" → the nav.migration tab). Used by the
    /// <c>--screen</c> startup deep-link so a specific screen can be shown on launch (handy for
    /// per-module screenshots and demos). Unknown keys are ignored; the default tab stays selected.
    /// </summary>
    public bool SelectNavByKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        string id = key.Trim().ToLowerInvariant();
        if (id.StartsWith("nav.", StringComparison.OrdinalIgnoreCase))
            id = id["nav.".Length..];

        foreach (NavItem item in Nav)
        {
            if (item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                SelectedNav = item;
                return true;
            }
        }

        return false;
    }

    /// <summary>Fire-and-forget startup loads for any nav content that opts in (IWckStartupAware).</summary>
    public void OnShellStartup()
    {
        foreach (NavItem item in Nav)
            if (item.Content is IWckStartupAware aware)
                _ = aware.OnShellStartupAsync();
    }

    private object HostContent(NavItem item)
    {
        if (!_modulesById.TryGetValue(item.Id, out IWckModule? module))
            return item.Content;

        if (_moduleViews.TryGetValue(item.Id, out FrameworkElement? cached))
            return cached;

        FrameworkElement? view = module.CreateView();
        if (view is null)
            return item.Content;

        view.DataContext = item.Content;
        _moduleViews[item.Id] = view;
        return view;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        private EmptyServiceProvider()
        {
        }

        public object? GetService(Type serviceType) => null;
    }
}

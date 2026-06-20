using System.Collections.ObjectModel;
using System.Windows.Input;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;

namespace WindowsCareKit.App.ViewModels;

/// <summary>The shell: the navigation rail, the selected content, and the language toggle.</summary>
public sealed class MainViewModel : ObservableObject
{
    private NavItem _selectedNav = null!;

    public MainViewModel(I18n i18n, UninstallViewModel uninstall, CleanViewModel clean,
        BackupViewModel backup, InstallViewModel install)
    {
        I18n = i18n;
        Uninstall = uninstall;

        // Glyphs are Segoe MDL2 Assets / Segoe Fluent Icons code points (delete / clean / save / download / gear).
        Nav = new ObservableCollection<NavItem>
        {
            new(i18n, "nav.uninstall", "", uninstall, "nav.uninstall.desc"),
            new(i18n, "nav.clean", "", clean, "nav.clean.desc"),
            new(i18n, "nav.backup", "", backup, "nav.backup.desc"),
            new(i18n, "nav.install", "", install, "nav.install.desc"),
            new(i18n, "nav.settings", "", new PlaceholderViewModel(i18n, "nav.settings"), isSettings: true),
        };

        ToggleLanguageCommand = new RelayCommand(() => I18n.Toggle());
        DismissFirstRunCommand = new RelayCommand(() => ShowFirstRun = false);
        SelectedNav = Nav[0];
    }

    public I18n I18n { get; }
    public UninstallViewModel Uninstall { get; }
    public ObservableCollection<NavItem> Nav { get; }
    public ICommand ToggleLanguageCommand { get; }
    public ICommand DismissFirstRunCommand { get; }

    private bool _showFirstRun = true;
    public bool ShowFirstRun { get => _showFirstRun; set => SetField(ref _showFirstRun, value); }

    public NavItem SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (SetField(ref _selectedNav, value))
                OnPropertyChanged(nameof(CurrentContent));
        }
    }

    public object CurrentContent => _selectedNav.Content;
}

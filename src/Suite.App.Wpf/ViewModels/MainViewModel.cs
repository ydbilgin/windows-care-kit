using System.Collections.ObjectModel;
using System.Windows.Input;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;

namespace WindowsCareKit.App.ViewModels;

/// <summary>The shell: the navigation rail, the selected content, and the language selector.</summary>
public sealed class MainViewModel : ObservableObject
{
    private NavItem _selectedNav = null!;

    public MainViewModel(I18n i18n, UninstallViewModel uninstall, CleanViewModel clean,
        BackupViewModel backup, MigrationViewModel migration, RestoreViewModel restore, InstallViewModel install,
        SettingsViewModel settings)
    {
        I18n = i18n;
        Uninstall = uninstall;
        Migration = migration;
        Restore = restore;
        Settings = settings;

        // Glyphs are Segoe MDL2 Assets / Segoe Fluent Icons code points (delete / clean / save / migrate / restore / download / gear).
        Nav = new ObservableCollection<NavItem>
        {
            new(i18n, "nav.uninstall", "", uninstall, "nav.uninstall.desc"),
            new(i18n, "nav.clean", "", clean, "nav.clean.desc"),
            new(i18n, "nav.backup", "", backup, "nav.backup.desc"),
            new(i18n, "nav.migration", "", migration, "nav.migration.desc"),
            new(i18n, "nav.restore", "", restore, "nav.restore.desc"),
            new(i18n, "nav.install", "", install, "nav.install.desc"),
            new(i18n, "nav.settings", "", settings, "nav.settings.desc", isSettings: true),
        };

        DismissFirstRunCommand = new RelayCommand(() => ShowFirstRun = false);
        SelectedNav = Nav[0];
    }

    public I18n I18n { get; }
    public UninstallViewModel Uninstall { get; }
    public MigrationViewModel Migration { get; }
    public RestoreViewModel Restore { get; }
    public SettingsViewModel Settings { get; }
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
                if (ReferenceEquals(value.Content, Migration))
                    _ = Migration.StartScanAsync();
            }
        }
    }

    public object CurrentContent => _selectedNav.Content;

    /// <summary>
    /// Opens a module by its short key (e.g. "migration" → the nav.migration tab). Used by the
    /// <c>--screen</c> startup deep-link so a specific screen can be shown on launch (handy for
    /// per-module screenshots and demos). Unknown keys are ignored; the default tab stays selected.
    /// </summary>
    public bool SelectNavByKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        string nameKey = "nav." + key.Trim().ToLowerInvariant();
        foreach (NavItem item in Nav)
        {
            if (item.NameKey.Equals(nameKey, StringComparison.OrdinalIgnoreCase))
            {
                SelectedNav = item;
                return true;
            }
        }

        return false;
    }
}

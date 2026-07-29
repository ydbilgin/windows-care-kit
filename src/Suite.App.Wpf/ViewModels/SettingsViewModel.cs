using System.Reflection;
using System.Windows.Input;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.App.Theming;
using WindowsCareKit.Core.Execution;

namespace WindowsCareKit.App.ViewModels;

public sealed record ThemeChoice(AppTheme Theme, string DisplayName);

/// <summary>One row of the Components card. Pure projection: no I/O, no policy.</summary>
public sealed record ModuleComponentRow(string Name, string StatusText, string ReasonText);

public sealed class SettingsViewModel : ObservableObject
{
    public const string LicenseName = "MIT";
    public const string ProjectRepositoryUrl = "https://github.com/ydbilgin/windows-care-kit";
    public const string ProjectReleasesUrl = "https://github.com/ydbilgin/windows-care-kit/releases";

    private readonly IThemeService _themeService;
    private readonly IUrlOpener _urlOpener;
    private readonly ModuleCatalogHealth _health;
    private bool _themeSaveFailed;

    public SettingsViewModel(
        I18n i18n,
        IThemeService themeService,
        IUrlOpener urlOpener,
        ModuleCatalogHealth health)
    {
        I18n = i18n ?? throw new ArgumentNullException(nameof(i18n));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _urlOpener = urlOpener ?? throw new ArgumentNullException(nameof(urlOpener));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        ModulesRoot = _health.ModulesRoot;
        OpenExternalLinkCommand = new RelayCommand(parameter =>
        {
            Uri? uri = parameter switch
            {
                Uri candidate when candidate.IsAbsoluteUri => candidate,
                string value when Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed) => parsed,
                _ => null
            };

            if (uri is null || !uri.IsAbsoluteUri ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return;

            _urlOpener.Open(uri);
        });
        Version = ResolveVersion(typeof(SettingsViewModel).Assembly);
        I18n.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "Item[]" or nameof(I18n.Culture) or nameof(I18n.SelectedCulture))
            {
                Raise(nameof(AvailableThemes));
                Raise(nameof(ThemeStatusText));
                RebuildModulePresentation();
            }
        };
        RebuildModulePresentation();
    }

    public I18n I18n { get; }
    public string Version { get; }
    public string License => LicenseName;
    public string RepositoryUrl => ProjectRepositoryUrl;
    public string ReleasesUrl => ProjectReleasesUrl;
    public ICommand OpenExternalLinkCommand { get; }
    public string ModulesRoot { get; }
    public IReadOnlyList<ModuleComponentRow> ModuleComponents { get; private set; } = [];
    public string ModulesEmptyNote { get; private set; } = string.Empty;
    public string ModulesInventoryNote { get; private set; } = string.Empty;
    public IReadOnlyList<ThemeChoice> AvailableThemes
        => _themeService.AvailableThemes
            .Select(theme => new ThemeChoice(theme, I18n[ThemeResourceKey(theme)]))
            .ToList();

    public AppTheme SelectedTheme
    {
        get => _themeService.SelectedTheme;
        set
        {
            if (value == _themeService.SelectedTheme)
                return;

            _themeSaveFailed = false;
            if (!_themeService.TrySelectTheme(value))
                _themeSaveFailed = true;

            NotifyThemeStateChanged();
        }
    }

    public bool RestartRequired => _themeService.RestartRequired;

    public string ThemeStatusText
    {
        get
        {
            if (_themeSaveFailed)
                return I18n["settings.theme.saveFailed"];

            return RestartRequired
                ? I18n["settings.theme.restartRequired"]
                : I18n["settings.theme.current"];
        }
    }

    internal static string ResolveVersion(Assembly assembly)
    {
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        string value = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString() ?? string.Empty
            : informational;

        return TrimBuildMetadata(value);
    }

    /// <summary>Drops SemVer build metadata (everything from the first '+') so the UI shows "1.2.3", not "1.2.3+sha".</summary>
    internal static string TrimBuildMetadata(string value)
    {
        int plus = value.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? value[..plus] : value;
    }

    private static string ThemeResourceKey(AppTheme theme)
        => theme == AppTheme.Light ? "theme.light" : "theme.dark";

    private void RebuildModulePresentation()
    {
        ModuleComponents = _health.Components.Select(record => record.Status switch
        {
            ModuleComponentStatus.Loaded => new ModuleComponentRow(
                record.DirectoryName,
                I18n["modules.status.loaded"],
                string.Empty),
            ModuleComponentStatus.Incomplete => new ModuleComponentRow(
                record.DirectoryName,
                I18n["modules.status.incomplete"],
                I18n["modules.reason.incomplete"]),
            ModuleComponentStatus.Malformed => new ModuleComponentRow(
                record.DirectoryName,
                I18n["modules.status.malformed"],
                I18n.Format(
                    "modules.reason.malformed",
                    record.FailureCategory ?? record.Status.ToString())),
            ModuleComponentStatus.Unreadable => new ModuleComponentRow(
                record.DirectoryName,
                I18n["modules.status.unreadable"],
                I18n.Format(
                    "modules.reason.unreadable",
                    record.FailureCategory ?? record.Status.ToString())),
            _ => throw new ArgumentOutOfRangeException(nameof(record.Status), record.Status, null),
        }).ToList();

        ModulesEmptyNote = _health.Status == ModuleInventoryStatus.NotInstalled
            ? I18n["modules.none"]
            : string.Empty;
        ModulesInventoryNote = _health.Status == ModuleInventoryStatus.Unavailable
            ? I18n.Format(
                "modules.inventory.unavailable",
                _health.FailureCategory ?? _health.Status.ToString())
            : string.Empty;

        Raise(nameof(ModulesRoot));
        Raise(nameof(ModuleComponents));
        Raise(nameof(ModulesEmptyNote));
        Raise(nameof(ModulesInventoryNote));
    }

    private void NotifyThemeStateChanged()
    {
        Raise(nameof(SelectedTheme));
        Raise(nameof(RestartRequired));
        Raise(nameof(ThemeStatusText));
    }
}

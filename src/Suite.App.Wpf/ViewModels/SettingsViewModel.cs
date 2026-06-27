using System.Reflection;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.App.Theming;

namespace WindowsCareKit.App.ViewModels;

public sealed record ThemeChoice(AppTheme Theme, string DisplayName);

public sealed class SettingsViewModel : ObservableObject
{
    public const string LicenseName = "MIT";
    public const string ProjectRepositoryUrl = "https://github.com/ydbilgin/windows-care-kit";
    public const string ProjectReleasesUrl = "https://github.com/ydbilgin/windows-care-kit/releases";

    private readonly IThemeService _themeService;
    private bool _themeSaveFailed;

    public SettingsViewModel(I18n i18n, IThemeService themeService)
    {
        I18n = i18n ?? throw new ArgumentNullException(nameof(i18n));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        Version = ResolveVersion(typeof(SettingsViewModel).Assembly);
        I18n.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "Item[]" or nameof(I18n.Culture) or nameof(I18n.SelectedCulture))
            {
                Raise(nameof(AvailableThemes));
                Raise(nameof(ThemeStatusText));
            }
        };
    }

    public I18n I18n { get; }
    public string Version { get; }
    public string License => LicenseName;
    public string RepositoryUrl => ProjectRepositoryUrl;
    public string ReleasesUrl => ProjectReleasesUrl;
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

    private void NotifyThemeStateChanged()
    {
        Raise(nameof(SelectedTheme));
        Raise(nameof(RestartRequired));
        Raise(nameof(ThemeStatusText));
    }
}

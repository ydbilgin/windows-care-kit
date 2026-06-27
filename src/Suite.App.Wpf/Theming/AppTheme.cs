using System.IO;
using System.Text.Json;
using System.Windows;

namespace WindowsCareKit.App.Theming;

public enum AppTheme
{
    Dark,
    Light
}

public interface IThemeService
{
    IReadOnlyList<AppTheme> AvailableThemes { get; }
    AppTheme SelectedTheme { get; }
    AppTheme AppliedTheme { get; }
    bool RestartRequired { get; }
    bool TrySelectTheme(AppTheme theme);
}

public sealed class ThemeService : IThemeService
{
    public const string EnvironmentVariableName = "WCK_THEME";
    public const string SettingsFileName = "settings.json";

    private static readonly IReadOnlyList<AppTheme> Themes = new[] { AppTheme.Dark, AppTheme.Light };
    private readonly IThemePreferenceStore _store;
    private AppTheme _selectedTheme;

    public ThemeService(string[] args, string? environmentTheme, IThemePreferenceStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        AppliedTheme = Resolve(args, environmentTheme, _store.TryReadTheme());
        _selectedTheme = AppliedTheme;
    }

    public IReadOnlyList<AppTheme> AvailableThemes => Themes;
    public AppTheme SelectedTheme => _selectedTheme;
    public AppTheme AppliedTheme { get; }
    public bool RestartRequired => SelectedTheme != AppliedTheme;

    public bool TrySelectTheme(AppTheme theme)
    {
        if (!Themes.Contains(theme))
            theme = AppTheme.Dark;

        if (theme == _selectedTheme)
            return true;

        if (!_store.TryWriteTheme(theme))
            return false;

        _selectedTheme = theme;
        return true;
    }

    public static AppTheme Resolve(string[] args, string? environmentTheme, AppTheme? persistedTheme)
    {
        string? cliValue = ExtractThemeArg(args);
        if (cliValue is not null)
            return ParseOrDefault(cliValue);

        if (environmentTheme is not null)
            return ParseOrDefault(environmentTheme);

        return persistedTheme ?? AppTheme.Dark;
    }

    public static AppTheme ParseOrDefault(string? value)
        => Parse(value) ?? AppTheme.Dark;

    private static string? ExtractThemeArg(string[] args)
        => App.ExtractOption(args, "--theme");

    private static AppTheme? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "dark" => AppTheme.Dark,
            "light" => AppTheme.Light,
            _ => null
        };
    }
}

public interface IThemePreferenceStore
{
    AppTheme? TryReadTheme();
    bool TryWriteTheme(AppTheme theme);
}

public sealed class JsonThemePreferenceStore : IThemePreferenceStore
{
    private readonly string _baseDirectory;

    public JsonThemePreferenceStore(string baseDirectory)
    {
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? DefaultBaseDirectory
            : baseDirectory;
    }

    public static string DefaultBaseDirectory
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsCareKit");

    public string SettingsPath => Path.Combine(_baseDirectory, ThemeService.SettingsFileName);

    public AppTheme? TryReadTheme()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return null;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (!doc.RootElement.TryGetProperty("theme", out JsonElement value) ||
                value.ValueKind != JsonValueKind.String)
            {
                if (!doc.RootElement.TryGetProperty("Theme", out value) ||
                    value.ValueKind != JsonValueKind.String)
                {
                    return null;
                }
            }

            return ThemeService.ParseOrDefault(value.GetString());
        }
        catch
        {
            return null;
        }
    }

    public bool TryWriteTheme(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(_baseDirectory);
            var payload = new ThemeSettings(theme.ToString());
            string json = JsonSerializer.Serialize(
                payload,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                });
            File.WriteAllText(SettingsPath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record ThemeSettings(string Theme);
}

public static class ThemeDictionary
{
    public static Uri SourceFor(AppTheme theme)
        => new(theme == AppTheme.Light ? "Themes/Daylight.xaml" : "Themes/Strongbox.xaml", UriKind.Relative);

    public static void ApplyStartupTheme(ResourceDictionary resources, AppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(resources);

        Uri desiredSource = SourceFor(theme);
        for (int i = 0; i < resources.MergedDictionaries.Count; i++)
        {
            ResourceDictionary dictionary = resources.MergedDictionaries[i];
            if (IsThemeDictionary(dictionary.Source))
            {
                if (!SourcesEqual(dictionary.Source, desiredSource))
                    resources.MergedDictionaries[i] = new ResourceDictionary { Source = desiredSource };
                return;
            }
        }

        resources.MergedDictionaries.Insert(0, new ResourceDictionary { Source = desiredSource });
    }

    private static bool IsThemeDictionary(Uri? source)
        => SourcesEqual(source, SourceFor(AppTheme.Dark)) || SourcesEqual(source, SourceFor(AppTheme.Light));

    private static bool SourcesEqual(Uri? left, Uri right)
        => left is not null &&
           string.Equals(left.OriginalString, right.OriginalString, StringComparison.OrdinalIgnoreCase);
}

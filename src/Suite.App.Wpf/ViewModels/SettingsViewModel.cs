using System.Reflection;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;

namespace WindowsCareKit.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    public const string LicenseName = "MIT";
    public const string ProjectRepositoryUrl = "https://github.com/ydbilgin/windows-care-kit";
    public const string ProjectReleasesUrl = "https://github.com/ydbilgin/windows-care-kit/releases";

    public SettingsViewModel(I18n i18n)
    {
        I18n = i18n ?? throw new ArgumentNullException(nameof(i18n));
        Version = ResolveVersion(typeof(SettingsViewModel).Assembly);
    }

    public I18n I18n { get; }
    public string Version { get; }
    public string License => LicenseName;
    public string RepositoryUrl => ProjectRepositoryUrl;
    public string ReleasesUrl => ProjectReleasesUrl;

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
}

using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using WpfApp = WindowsCareKit.App.App;

namespace WindowsCareKit.Tests;

/// <summary>
/// Builds an <see cref="I18n"/> loaded through the real production merge (modular M2b): the shell base
/// file plus every default module's embedded fragment. Tests that assert MODULE-owned strings (not just
/// shell chrome) must use this instead of a bare <c>new I18n()</c>, which only ever sees shell keys.
/// </summary>
internal static class TestI18n
{
    public static I18n Full(string culture = "en")
    {
        I18n i18n = new(
            Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang"),
            WpfApp.CreateDefaultModules());
        i18n.Load(culture);
        return i18n;
    }

    private static string RepoRoot
    {
        get
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WindowsCareKit.slnx")))
                dir = dir.Parent;

            return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
        }
    }
}

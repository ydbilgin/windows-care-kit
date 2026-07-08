using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using Xunit;
using WpfApp = WindowsCareKit.App.App;

namespace WindowsCareKit.Tests;

/// <summary>
/// The M2b gate: the shell + six module lang fragments merge, through the real production path
/// (<see cref="TestI18n"/>), into a table identical to the pre-split monolith minus the one dead
/// <c>placeholder.body</c> key — and a key parked in the wrong module's fragment is caught even though
/// the merged set alone would hide it (the M4 insurance, SPEC §B4-3).
/// </summary>
public sealed class LangFragmentCompositionTests
{
    private const int ExpectedMergedKeyCount = 368;

    private static readonly Regex XamlKeyRegex = new(@"I18n\[([a-z][A-Za-z0-9_.]+)\]", RegexOptions.Compiled);
    private static readonly Regex CsKeyRegex =
        new(@"(?:I18n|i18n)(?:\[|\.Format\()""([a-z][A-Za-z0-9_.]+)""", RegexOptions.Compiled);

    private static readonly (string ModuleId, string Project)[] Modules =
    [
        ("uninstall", "Suite.Module.Uninstall"),
        ("clean", "Suite.Module.Clean"),
        ("backup", "Suite.Module.Backup"),
        ("migration", "Suite.Module.Migration"),
        ("restore", "Suite.Module.Restore"),
        ("install", "Suite.Module.Install"),
    ];

    [Fact]
    public void Merged_runtime_map_has_exactly_368_keys_no_meta_no_placeholder_and_en_tr_parity()
    {
        I18n en = TestI18n.Full("en");
        I18n tr = TestI18n.Full("tr");

        Assert.Equal(ExpectedMergedKeyCount, en.Map.Count);
        Assert.Equal(
            en.Map.Keys.Order(StringComparer.Ordinal),
            tr.Map.Keys.Order(StringComparer.Ordinal));

        Assert.DoesNotContain(en.Map.Keys, k => k.StartsWith("meta.", StringComparison.Ordinal));
        Assert.DoesNotContain(en.Map.Keys, k => k == "placeholder.body");
        Assert.DoesNotContain(tr.Map.Keys, k => k.StartsWith("meta.", StringComparison.Ordinal));
        Assert.DoesNotContain(tr.Map.Keys, k => k == "placeholder.body");
    }

    [Fact]
    public void Module_fragment_colliding_with_a_shell_base_key_hard_fails_at_load()
    {
        var fake = new FakeLangModule("fake-collides-with-shell",
            new Dictionary<string, string> { ["app.title"] = "Collision" });

        I18n i18n = new(ShellLangDir, new IWckModule[] { fake });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => i18n.Load("en"));
        Assert.Contains("app.title", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_module_fragments_sharing_a_key_hard_fail_at_load()
    {
        var a = new FakeLangModule("fake-a", new Dictionary<string, string> { ["fake.shared.key"] = "A" });
        var b = new FakeLangModule("fake-b", new Dictionary<string, string> { ["fake.shared.key"] = "B" });

        I18n i18n = new(ShellLangDir, new IWckModule[] { a, b });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => i18n.Load("en"));
        Assert.Contains("fake.shared.key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_module_only_references_keys_it_owns_or_the_shell_base_defines()
    {
        HashSet<string> shellEnKeys = ReadJsonKeys(Path.Combine(ShellLangDir, "en.json"));

        foreach ((string moduleId, string project) in Modules)
        {
            string moduleRoot = Path.Combine(RepoRoot, "src", project);
            HashSet<string> ownEnKeys = ReadJsonKeys(Path.Combine(moduleRoot, "lang", "en.json"));
            HashSet<string> allowed = new(ownEnKeys, StringComparer.Ordinal);
            allowed.UnionWith(shellEnKeys);

            HashSet<string> referenced = ScanReferencedKeys(moduleRoot);
            string[] orphans = referenced.Where(k => !allowed.Contains(k)).Order(StringComparer.Ordinal).ToArray();

            Assert.True(orphans.Length == 0,
                $"module '{moduleId}' statically references key(s) not in its own fragment or the shell base: " +
                string.Join(", ", orphans));
        }
    }

    [Fact]
    public void Shell_and_abstractions_only_reference_shell_base_keys()
    {
        HashSet<string> shellEnKeys = ReadJsonKeys(Path.Combine(ShellLangDir, "en.json"));

        foreach (string project in new[] { "Suite.App.Wpf", "Suite.App.Abstractions" })
        {
            string root = Path.Combine(RepoRoot, "src", project);
            HashSet<string> referenced = ScanReferencedKeys(root);
            string[] orphans = referenced.Where(k => !shellEnKeys.Contains(k)).Order(StringComparer.Ordinal).ToArray();

            Assert.True(orphans.Length == 0,
                $"'{project}' statically references key(s) not in the shell base file: " + string.Join(", ", orphans));
        }
    }

    [Fact]
    public void Every_catalog_modules_titleKey_and_descKey_resolve_in_the_merged_map()
    {
        I18n en = TestI18n.Full("en");

        foreach (IWckModule module in WpfApp.CreateDefaultModules())
        {
            Assert.True(en.Map.ContainsKey(module.TitleKey),
                $"module '{module.Id}' TitleKey '{module.TitleKey}' does not resolve in the merged map");
            Assert.True(en.Map.ContainsKey(module.DescKey),
                $"module '{module.Id}' DescKey '{module.DescKey}' does not resolve in the merged map");
        }
    }

    private static HashSet<string> ScanReferencedKeys(string root)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(root))
            return keys;

        foreach (string file in EnumerateSourceFiles(root, "*.xaml"))
            foreach (Match m in XamlKeyRegex.Matches(File.ReadAllText(file)))
                keys.Add(m.Groups[1].Value);

        foreach (string file in EnumerateSourceFiles(root, "*.cs"))
            foreach (Match m in CsKeyRegex.Matches(File.ReadAllText(file)))
                keys.Add(m.Groups[1].Value);

        return keys;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root, string pattern)
    {
        foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => p is "bin" or "obj"))
                continue;
            yield return file;
        }
    }

    private static HashSet<string> ReadJsonKeys(string path)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(k => k != "meta.languageName")
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ShellLangDir => Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang");

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

    private sealed class FakeLangModule(string id, IReadOnlyDictionary<string, string> enFragment) : IWckModule
    {
        public string Id => id;
        public string TitleKey => "nav." + id;
        public string DescKey => "nav." + id + ".desc";
        public string IconKey => "";
        public int Order => 0;
        public bool IsSettings => false;

        public void RegisterServices(IServiceCollection services)
        {
        }

        public object CreateContent(IServiceProvider sp) => new();

        public FrameworkElement? CreateView() => null;

        public IReadOnlyDictionary<string, string> GetLangFragment(string culture)
            => culture == "en" ? enFragment : new Dictionary<string, string>();
    }
}

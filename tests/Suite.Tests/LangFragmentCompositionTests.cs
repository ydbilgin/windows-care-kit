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
    // PR-1 (Fable design §A4/§A6, 2026-07-08) added four migration app-row keys; S7 adds NotRestored;
    // G6 (2026-07-11, committed) adds 21 live-localized plan.* keys (verb/risk/undo/blocked/whole-tree);
    // G-m3 (2026-07-11) adds 2 uninstall.inventory.* degraded-notice keys;
    // G-m2 adds one net preview/result split key to Backup and one preview key to Migration.
    // NEW-07 (Round 3B2, 2026-07-23) adds 3 Clean source-health-note keys (recycle/startup/extensions).
    // Manifest honesty adds 2 Install and 3 Backup health-note keys.
    // MINOR-03 (2026-07-29) adds 2 Install and 2 Backup manifest failure-cause clauses (corrupt/unreadable),
    // so the actionable difference stops being carried only by an untranslated CLR type name.
    // Catalog health adds 14 shell-owned component inventory/status/reason/notice keys.
    // M2 (Clean screen, 2026-08-06) adds 17 Clean keys — StatePill state words, the ReviewSummary rail's
    // labels/recovery lines/approve label, the Technical-details toggle and the three consequence sentences —
    // and retires 2: clean.junk.run (the veto-blind whole-plan run button is gone, ApprovePlanCommand is the
    // one door) and clean.recycle.irreversible (the row's UndoLine states the same fact once).
    private const int ExpectedMergedKeyCount = 441;

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
    public void Merged_runtime_map_has_expected_key_count_no_meta_no_placeholder_and_en_tr_parity()
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

    /// <summary>
    /// The fitness check for <see cref="BaseStringTable.RequiredKeys"/>: the floor a shipped base table has
    /// to clear must keep covering the shell as the shell grows. Without this, adding a label to the shell
    /// silently narrows the floor — the table could ship without that key, the gate would still say
    /// <c>Ok</c>, and the new label alone would render as its raw key.
    /// </summary>
    [Fact]
    public void Every_shell_key_the_source_references_is_required_of_the_shipped_base_table()
    {
        var required = BaseStringTable.RequiredKeys.ToHashSet(StringComparer.Ordinal);

        foreach (string project in new[] { "Suite.App.Wpf", "Suite.App.Abstractions" })
        {
            string[] uncovered = ScanReferencedKeys(Path.Combine(RepoRoot, "src", project))
                .Where(k => !required.Contains(k))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.True(uncovered.Length == 0,
                $"'{project}' renders key(s) that BaseStringTable.RequiredKeys does not require of the base " +
                "table, so a table shipped without them would still pass the startup gate: " +
                string.Join(", ", uncovered));
        }
    }

    /// <summary>
    /// The half the source scan cannot see: the scan only matches a key written as a literal inside the
    /// lookup, and the shell reaches plenty of its keys through a variable instead — <c>PlanRow</c> builds
    /// <c>"plan.risk." + the enum name</c>, <c>SettingsViewModel</c> picks <c>theme.light</c>/<c>theme.dark</c>
    /// in a helper. Those are shell strings all the same, so the floor is the shell table's whole key set.
    /// Pinning the equality is what makes adding or removing a shell string a decision about the floor
    /// rather than an accident to it.
    /// </summary>
    [Fact]
    public void The_required_key_floor_is_exactly_the_shipped_shell_tables_own_keys()
    {
        Assert.Equal(
            BaseStringTable.RequiredKeys.Count,
            BaseStringTable.RequiredKeys.Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(
            ReadJsonKeys(Path.Combine(ShellLangDir, "en.json")).Order(StringComparer.Ordinal),
            BaseStringTable.RequiredKeys.Order(StringComparer.Ordinal));
    }

    /// <summary>A floor the product's own artifact cannot clear would be a rule that only ever fails in the
    /// field, so the shipped table is run through the production loader itself.</summary>
    [Fact]
    public void The_shipped_shell_table_clears_the_floor_it_is_judged_by()
    {
        BaseStringTableResult result =
            BaseStringTable.Load(Path.Combine(ShellLangDir, BaseStringTable.FileName));

        Assert.Equal(BaseStringTableStatus.Loaded, result.Status);
        foreach (string key in BaseStringTable.RequiredKeys)
            Assert.False(string.IsNullOrWhiteSpace(result.Entries[key]), key + " is blank in the shipped table");
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

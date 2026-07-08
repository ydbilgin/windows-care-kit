using System.Text.Json;
using System.Xml.Linq;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Selection;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Localization keys are now partitioned across the shell base file and six module-owned fragments
/// (modular M2b). These facts run per-SOURCE (shell pair + each module's fragment pair) instead of
/// against one monolith, and key-existence facts are sharpened to the fragment that actually OWNS the
/// key (SPEC §D3) — a key checked against the wrong source would pass vacuously.
/// </summary>
public sealed class MigrationLocalizationTests
{
    [Fact]
    public void Non_english_language_files_do_not_define_keys_missing_from_english()
    {
        var orphanKeys = new List<string>();

        foreach ((string label, string dir) in Sources())
        {
            HashSet<string> english = ReadKeys(Path.Combine(dir, "en.json"));

            foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            {
                string code = Path.GetFileNameWithoutExtension(file);
                if (code.Equals("en", StringComparison.OrdinalIgnoreCase))
                    continue;

                // meta.* keys are file-descriptive (e.g. meta.languageName), not UI content — a language
                // may carry its own without it being an "orphan" against English.
                foreach (string key in ReadKeys(file).Except(english)
                             .Where(k => !k.StartsWith("meta.", StringComparison.Ordinal)).Order())
                    orphanKeys.Add($"{label}:{code}:{key}");
            }
        }

        Assert.Empty(orphanKeys);
    }

    [Fact]
    public void Shipped_language_files_define_identical_key_sets()
    {
        foreach ((string _, string dir) in Sources())
        {
            string[] english = ReadKeys(Path.Combine(dir, "en.json")).Order().ToArray();

            foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            {
                string[] keys = ReadKeys(file).Order().ToArray();
                Assert.Equal(english, keys);
            }
        }
    }

    [Fact]
    public void Merged_en_and_tr_runtime_tables_carry_identical_key_sets()
    {
        WindowsCareKit.App.Localization.I18n en = TestI18n.Full("en");
        WindowsCareKit.App.Localization.I18n tr = TestI18n.Full("tr");

        Assert.Equal(
            en.Map.Keys.Order(StringComparer.Ordinal),
            tr.Map.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Backup_screen_localization_keys_exist_and_view_has_no_literal_text_labels()
    {
        string backupLangDir = ModuleLangDir("Suite.Module.Backup");
        string viewPath = Path.Combine(FindRepositoryRoot(), "src", "Suite.Module.Backup", "Views", "BackupView.xaml");
        string xaml = File.ReadAllText(viewPath);
        string[] expected =
        [
            "backup.report.title",
            "backup.report.manual",
            "backup.report.skipped",
            "backup.report.copied",
            "backup.dryRun.badge",
            "backup.dryRun.caption",
            "backup.row.cantCarry",
            "backup.row.medChip",
            "backup.row.skipChip",
            "backup.count.toCopy",
            "backup.count.manual",
            "backup.count.skipped",
            "backup.summary.title",
            "backup.summary.toCopy",
            "backup.summary.manual",
            "backup.summary.skipped",
            "backup.summary.destination",
            "backup.summary.trustPrefix",
            "backup.summary.trustStrong",
            "backup.summary.statusPrefix",
            "backup.summary.statusDryRun",
            "backup.footer",
        ];

        foreach (string file in Directory.EnumerateFiles(backupLangDir, "*.json"))
        {
            HashSet<string> keys = ReadKeys(file);
            Assert.All(expected, key => Assert.Contains(key, keys));
        }

        Assert.All(expected, key => Assert.Contains($"I18n[{key}]", xaml));
        Assert.DoesNotContain("backup.plan.willCopy", xaml);
        Assert.Empty(UserVisibleLiteralAttributes(viewPath));
    }

    [Fact]
    public void Backup_result_rows_bind_chip_brush_to_row_outcome()
    {
        string viewPath = Path.Combine(FindRepositoryRoot(), "src", "Suite.Module.Backup", "Views", "BackupView.xaml");
        XNamespace xamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument document = XDocument.Load(viewPath);
        XElement resultTemplate = document.Descendants()
            .Single(e => e.Name.LocalName == "DataTemplate" &&
                         (string?)e.Attribute(xamlNs + "Key") == "ResultRowTemplate");

        Assert.Contains(resultTemplate.Descendants(), e =>
            e.Name.LocalName == "Border" &&
            (string?)e.Attribute("Background") == "{Binding RiskBrush}" &&
            (string?)e.Attribute("BorderBrush") == "{Binding RiskBrush}");

        Assert.Contains(document.Descendants(), e =>
            e.Name.LocalName == "ItemsControl" &&
            (string?)e.Attribute("ItemsSource") == "{Binding ResultRows}" &&
            (string?)e.Attribute("ItemTemplate") == "{StaticResource ResultRowTemplate}");
    }

    // Orphan checks alone would still pass if a key were missing from every file. The runtime
    // builds these keys dynamically from enum values (MigrationSourceRow.Text, the group headers), so a new enum
    // member would silently render its raw key. Assert every enum-derived key actually exists in the OWNING
    // fragment's English file: scan.source/group ∈ Migration, restore.disposition ∈ Restore (SPEC §D3).
    [Fact]
    public void Every_enum_derived_migration_key_exists_in_english()
    {
        HashSet<string> migrationEnglish = ReadKeys(Path.Combine(ModuleLangDir("Suite.Module.Migration"), "en.json"));
        HashSet<string> restoreEnglish = ReadKeys(Path.Combine(ModuleLangDir("Suite.Module.Restore"), "en.json"));

        var expectedInMigration = new List<string>();
        foreach (ProgramSourceKind kind in Enum.GetValues<ProgramSourceKind>())
            expectedInMigration.Add($"migration.scan.source.{kind}");
        foreach (ProgramSourceStatus status in Enum.GetValues<ProgramSourceStatus>())
            expectedInMigration.Add($"migration.scan.source.{status}");
        foreach (MigrationCategory category in Enum.GetValues<MigrationCategory>())
        {
            expectedInMigration.Add($"migration.group.{category}.title");
            expectedInMigration.Add($"migration.group.{category}.subtitle");
        }

        var expectedInRestore = new List<string>();
        foreach (RestoreDisposition disposition in Enum.GetValues<RestoreDisposition>())
            expectedInRestore.Add($"migration.restore.disposition.{disposition}");

        string[] missingMigration = expectedInMigration.Where(key => !migrationEnglish.Contains(key)).Order().ToArray();
        string[] missingRestore = expectedInRestore.Where(key => !restoreEnglish.Contains(key)).Order().ToArray();

        Assert.Empty(missingMigration);
        Assert.Empty(missingRestore);
    }

    [Fact]
    public void Capture_keys_exist_in_english_and_dead_restore_keys_are_removed()
    {
        HashSet<string> migrationEnglish = ReadKeys(Path.Combine(ModuleLangDir("Suite.Module.Migration"), "en.json"));
        string[] expected =
        [
            "migration.capture.title",
            "migration.capture.destination",
            "migration.capture.chooseFolder",
            "migration.capture.buildPlan",
            "migration.capture.approve",
            "migration.capture.run",
            "migration.capture.plan",
            "migration.capture.skipped",
            "migration.capture.results",
            "migration.capture.resultSummary",
            "migration.capture.outsideAppWarning",
            "migration.capture.refused",
        ];

        Assert.All(expected, key => Assert.Contains(key, migrationEnglish));

        // Dead keys must be gone everywhere — run against the merged union of every source, not just one.
        foreach ((string _, string dir) in Sources())
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            {
                HashSet<string> keys = ReadKeys(file);
                Assert.DoesNotContain("migration.button.restore", keys);
                Assert.DoesNotContain("migration.restore.disabledHelper", keys);
            }
        }
    }

    [Fact]
    public void Restore_screen_keys_exist_in_english()
    {
        HashSet<string> restoreEnglish = ReadKeys(Path.Combine(ModuleLangDir("Suite.Module.Restore"), "en.json"));
        string[] expected =
        [
            "nav.restore",
            "nav.restore.desc",
            "migration.restore.eyebrow",
            "migration.restore.title",
            "migration.restore.subtitle",
            "migration.restore.packageFolder",
            "migration.restore.packageHint",
            "migration.restore.chooseFolder",
            "migration.restore.buildPlan",
            "migration.restore.stateFolder",
            "migration.restore.invalidPackageWarning",
            "migration.restore.noManifestWarning",
            "migration.restore.note.title",
            "migration.restore.note.body",
            "migration.restore.plan",
            "migration.restore.planHint",
            "migration.restore.approve",
            "migration.restore.run",
            "migration.restore.planRows",
            "migration.restore.skipped",
            "migration.restore.dispositions",
            "migration.restore.disposition.RestorePlanned",
            "migration.restore.disposition.Restored",
            "migration.restore.disposition.ReinstallEnqueued",
            "migration.restore.disposition.Manual",
            "migration.restore.results",
            "migration.restore.undo.title",
            "migration.restore.undo.body",
            "migration.restore.undo.preview",
            "migration.restore.undo.approve",
            "migration.restore.undo.run",
            "migration.restore.previewSummary",
            "migration.restore.resultSummary",
            "migration.restore.undoSummary",
            "migration.restore.refused",
            "migration.restore.status.skipped",
            "migration.restore.status.Done",
            "migration.restore.status.Blocked",
            "migration.restore.status.Failed",
            "migration.restore.status.NotRun",
            "migration.restore.status.rejected",
        ];

        Assert.All(expected, key => Assert.Contains(key, restoreEnglish));
    }

    [Fact]
    public void Settings_screen_keys_exist_in_english()
    {
        HashSet<string> shellEnglish = ReadKeys(Path.Combine(LangDir(), "en.json"));
        string[] expected =
        [
            "nav.settings",
            "nav.settings.desc",
            "settings.eyebrow",
            "settings.title",
            "settings.subtitle",
            "settings.language.title",
            "settings.language.body",
            "settings.theme.title",
            "settings.theme.body",
            "settings.theme.current",
            "settings.theme.restartRequired",
            "settings.theme.saveFailed",
            "theme.dark",
            "theme.light",
            "settings.about.title",
            "settings.about.body",
            "settings.about.app",
            "settings.about.version",
            "settings.about.license",
            "settings.about.repository",
            "settings.about.repositoryLink",
            "settings.about.releases",
            "settings.about.releasesLink",
        ];

        Assert.All(expected, key => Assert.Contains(key, shellEnglish));
    }

    private static IEnumerable<(string Label, string Dir)> Sources()
    {
        yield return ("shell", LangDir());
        yield return ("Suite.Module.Uninstall", ModuleLangDir("Suite.Module.Uninstall"));
        yield return ("Suite.Module.Clean", ModuleLangDir("Suite.Module.Clean"));
        yield return ("Suite.Module.Backup", ModuleLangDir("Suite.Module.Backup"));
        yield return ("Suite.Module.Migration", ModuleLangDir("Suite.Module.Migration"));
        yield return ("Suite.Module.Restore", ModuleLangDir("Suite.Module.Restore"));
        yield return ("Suite.Module.Install", ModuleLangDir("Suite.Module.Install"));
    }

    private static HashSet<string> ReadKeys(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string[] UserVisibleLiteralAttributes(string path)
    {
        XDocument document = XDocument.Load(path);
        return document.Descendants()
            .Attributes()
            .Where(attribute => attribute.Name.LocalName is "Text" or "Content" or "Header")
            .Select(attribute => attribute.Value.Trim())
            .Where(value => value.Length > 0 &&
                            !value.StartsWith("{Binding", StringComparison.Ordinal) &&
                            value.Any(char.IsLetter))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string LangDir()
        => Path.Combine(FindRepositoryRoot(), "src", "Suite.App.Wpf", "lang");

    private static string ModuleLangDir(string project)
        => Path.Combine(FindRepositoryRoot(), "src", project, "lang");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WindowsCareKit.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("repository root not found");
    }
}

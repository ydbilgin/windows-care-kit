using System.Text.Json;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Selection;
using Xunit;

namespace WindowsCareKit.Tests;

public sealed class MigrationLocalizationTests
{
    [Fact]
    public void English_and_turkish_migration_keys_have_exact_parity()
    {
        string root = FindRepositoryRoot();
        HashSet<string> english = ReadKeys(Path.Combine(root, "src", "Suite.App.Wpf", "lang", "en.json"));
        HashSet<string> turkish = ReadKeys(Path.Combine(root, "src", "Suite.App.Wpf", "lang", "tr.json"));

        string[] enMigration = english.Where(IsMigrationKey).Order().ToArray();
        string[] trMigration = turkish.Where(IsMigrationKey).Order().ToArray();

        Assert.NotEmpty(enMigration);
        Assert.Equal(enMigration, trMigration);
    }

    // Parity alone (above) would still pass if a key were missing from BOTH files (auditor MINOR-1). The runtime
    // builds these keys dynamically from enum values (MigrationSourceRow.Text, the group headers), so a new enum
    // member would silently render its raw key. Assert every enum-derived key actually exists in BOTH lang files.
    [Fact]
    public void Every_enum_derived_migration_key_exists_in_both_languages()
    {
        string root = FindRepositoryRoot();
        HashSet<string> english = ReadKeys(Path.Combine(root, "src", "Suite.App.Wpf", "lang", "en.json"));
        HashSet<string> turkish = ReadKeys(Path.Combine(root, "src", "Suite.App.Wpf", "lang", "tr.json"));

        var expected = new List<string>();
        foreach (ProgramSourceKind kind in Enum.GetValues<ProgramSourceKind>())
            expected.Add($"migration.scan.source.{kind}");
        foreach (ProgramSourceStatus status in Enum.GetValues<ProgramSourceStatus>())
            expected.Add($"migration.scan.source.{status}");
        foreach (MigrationCategory category in Enum.GetValues<MigrationCategory>())
        {
            expected.Add($"migration.group.{category}.title");
            expected.Add($"migration.group.{category}.subtitle");
        }
        foreach (RestoreDisposition disposition in Enum.GetValues<RestoreDisposition>())
            expected.Add($"migration.restore.disposition.{disposition}");

        string[] missingEnglish = expected.Where(key => !english.Contains(key)).Order().ToArray();
        string[] missingTurkish = expected.Where(key => !turkish.Contains(key)).Order().ToArray();

        Assert.Empty(missingEnglish);
        Assert.Empty(missingTurkish);
    }

    [Fact]
    public void Capture_keys_exist_in_both_languages_and_dead_restore_keys_are_removed()
    {
        string root = FindRepositoryRoot();
        HashSet<string> english = ReadKeys(Path.Combine(root, "src", "Suite.App.Wpf", "lang", "en.json"));
        HashSet<string> turkish = ReadKeys(Path.Combine(root, "src", "Suite.App.Wpf", "lang", "tr.json"));
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

        Assert.All(expected, key =>
        {
            Assert.Contains(key, english);
            Assert.Contains(key, turkish);
        });
        Assert.DoesNotContain("migration.button.restore", english);
        Assert.DoesNotContain("migration.button.restore", turkish);
        Assert.DoesNotContain("migration.restore.disabledHelper", english);
        Assert.DoesNotContain("migration.restore.disabledHelper", turkish);
    }

    [Fact]
    public void Restore_screen_keys_exist_in_both_languages()
    {
        string root = FindRepositoryRoot();
        HashSet<string> english = ReadKeys(Path.Combine(root, "src", "Suite.App.Wpf", "lang", "en.json"));
        HashSet<string> turkish = ReadKeys(Path.Combine(root, "src", "Suite.App.Wpf", "lang", "tr.json"));
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
            "migration.restore.disposition.Restored",
            "migration.restore.disposition.ReinstallEnqueued",
            "migration.restore.disposition.Manual",
            "migration.restore.results",
            "migration.restore.undo.title",
            "migration.restore.undo.body",
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

        Assert.All(expected, key =>
        {
            Assert.Contains(key, english);
            Assert.Contains(key, turkish);
        });
    }

    private static bool IsMigrationKey(string key)
        => key.StartsWith("migration.", StringComparison.Ordinal)
           || key is "nav.migration" or "nav.migration.desc" or "nav.restore" or "nav.restore.desc";

    private static HashSet<string> ReadKeys(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

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

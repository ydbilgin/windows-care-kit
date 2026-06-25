using System.Text.Json;
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

        string[] missingEnglish = expected.Where(key => !english.Contains(key)).Order().ToArray();
        string[] missingTurkish = expected.Where(key => !turkish.Contains(key)).Order().ToArray();

        Assert.Empty(missingEnglish);
        Assert.Empty(missingTurkish);
    }

    private static bool IsMigrationKey(string key)
        => key.StartsWith("migration.", StringComparison.Ordinal)
           || key is "nav.migration" or "nav.migration.desc";

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

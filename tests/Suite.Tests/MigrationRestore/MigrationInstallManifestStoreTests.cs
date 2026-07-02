using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using Xunit;
using WindowsCareKit.Tests.TestInfra;

namespace WindowsCareKit.Tests.MigrationRestore;

/// <summary>
/// The STRICT migration package install loader (<see cref="MigrationInstallManifestStore"/>). The migration
/// package is UNTRUSTED, so this is the opposite of the Kur module's PERMISSIVE <see cref="InstallManifestLoader"/>:
/// a malformed/unknown-field/unknown-version package manifest THROWS (it never returns a silently-empty success),
/// while an ABSENT file is the legitimate "no installable apps" case → <see cref="InstallManifest.Empty"/>.
/// These tests pin that package data goes through the STRICT path, not the permissive one (critic fix #3).
/// </summary>
public class MigrationInstallManifestStoreTests
{
    private static string TempDir() => MigrationRestoreTestData.TempDir("install-store");

    private static void Write(string dir, string json)
        => File.WriteAllText(new MigrationInstallManifestStore().PathFor(dir), json);

    [Fact]
    public void Absent_file_returns_Empty_not_a_throw()
    {
        string dir = TempDir();
        try
        {
            // A package may legitimately carry no installable apps.
            InstallManifest m = new MigrationInstallManifestStore().Load(dir);
            Assert.Empty(m.Entries);
        }
        finally { TestFs.DeleteResilient(dir); }
    }

    [Fact]
    public void Roundtrips_a_valid_envelope()
    {
        string dir = TempDir();
        try
        {
            var store = new MigrationInstallManifestStore();
            store.Save(dir, new[]
            {
                new InstallEntry("migration:git.config:install", "install", "dev-tools", InstallMethod.Winget,
                    "Git.Git", null, RequiresAdmin: true, RebootExpected: false, RestoreOrder: 0, Description: "Git"),
            });

            InstallManifest m = store.Load(dir);
            InstallEntry e = Assert.Single(m.Entries);
            Assert.Equal("migration:git.config:install", e.Id);
            Assert.Equal(InstallMethod.Winget, e.Method);
            Assert.Equal("Git.Git", e.WingetId);
            Assert.True(e.RequiresAdmin);
        }
        finally { TestFs.DeleteResilient(dir); }
    }

    [Fact]
    public void Empty_entries_envelope_loads_as_empty()
    {
        string dir = TempDir();
        try
        {
            new MigrationInstallManifestStore().Save(dir, Array.Empty<InstallEntry>());
            Assert.Empty(new MigrationInstallManifestStore().Load(dir).Entries);
        }
        finally { TestFs.DeleteResilient(dir); }
    }

    [Fact]
    public void Rejects_unknown_schema_version()
    {
        string dir = TempDir();
        try
        {
            Write(dir, """{ "schemaVersion": 99, "entries": [] }""");
            Assert.Throws<MigrationManifestException>(() => new MigrationInstallManifestStore().Load(dir));
        }
        finally { TestFs.DeleteResilient(dir); }
    }

    [Fact]
    public void Rejects_an_unknown_field_does_not_silently_drop_it()
    {
        string dir = TempDir();
        try
        {
            // UnmappedMemberHandling.Disallow: a smuggled field FAILS the load (the permissive loader would skip it).
            Write(dir, """
                { "schemaVersion": 1, "entries": [
                  { "id": "migration:x:install", "method": "install-winget", "wingetId": "Git.Git", "command": "rm -rf /" }
                ] }
                """);
            Assert.Throws<MigrationManifestException>(() => new MigrationInstallManifestStore().Load(dir));
        }
        finally { TestFs.DeleteResilient(dir); }
    }

    [Fact]
    public void Rejects_a_malformed_entry_throws_not_returns_empty()
    {
        string dir = TempDir();
        try
        {
            // A winget entry whose id is a path-shaped value (the loader only trims; the strict store re-validates).
            Write(dir, """
                { "schemaVersion": 1, "entries": [
                  { "id": "migration:x:install", "method": "install-winget", "wingetId": "evil/../../id" }
                ] }
                """);
            Assert.Throws<MigrationManifestException>(() => new MigrationInstallManifestStore().Load(dir));
        }
        finally { TestFs.DeleteResilient(dir); }
    }

    [Fact]
    public void Rejects_multiple_locators()
    {
        string dir = TempDir();
        try
        {
            Write(dir, """
                { "schemaVersion": 1, "entries": [
                  { "id": "migration:x:install", "method": "install-winget", "wingetId": "Git.Git", "npmPackage": "left-pad" }
                ] }
                """);
            Assert.Throws<MigrationManifestException>(() => new MigrationInstallManifestStore().Load(dir));
        }
        finally { TestFs.DeleteResilient(dir); }
    }

    [Fact]
    public void Rejects_malformed_json_throws()
    {
        string dir = TempDir();
        try
        {
            Write(dir, "{ not json ");
            Assert.Throws<MigrationManifestException>(() => new MigrationInstallManifestStore().Load(dir));
        }
        finally { TestFs.DeleteResilient(dir); }
    }
}

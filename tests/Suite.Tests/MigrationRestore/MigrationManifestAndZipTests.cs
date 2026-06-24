using WindowsCareKit.Core.Modules.Migration;
using Xunit;

namespace WindowsCareKit.Tests.MigrationRestore;

/// <summary>
/// F5 (manifest validation: traversal/absolute/escape rejection on load) + the .zip export/import round-trip
/// (folder ↔ zip content is preserved; Zip-Slip is refused).
/// </summary>
public class MigrationManifestAndZipTests
{
    private static string TempDir() => MigrationRestoreTestData.TempDir("manzip");

    private static MigrationRestoreManifest ManifestWith(string relativePath, string source = "migration/x/f.cfg")
        => new(1, new[]
        {
            new MigrationRestoreTarget("git.config", "git.config#0", KnownFolder.UserProfile,
                relativePath, source, RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite,
                System.Array.Empty<string>(), PortabilityClass.ProfileRelative, "sha"),
        });

    [Fact]
    public void Manifest_round_trips_through_json()
    {
        string dir = TempDir();
        try
        {
            var store = new MigrationRestoreManifestStore();
            store.Save(dir, ManifestWith(".gitconfig"));
            MigrationRestoreManifest loaded = store.Load(dir);

            Assert.Equal(1, loaded.SchemaVersion);
            MigrationRestoreTarget t = Assert.Single(loaded.Targets);
            Assert.Equal("git.config", t.RecipeId);
            Assert.Equal(KnownFolder.UserProfile, t.KnownFolder);
            Assert.Equal(".gitconfig", t.RelativePath);
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Manifest_round_trips_restore_tier_and_manual_metadata()
    {
        string dir = TempDir();
        try
        {
            var target = new MigrationRestoreTarget("manual.app", "manual.app#0", KnownFolder.UserProfile,
                "prefs.json", "migration/manual.app/prefs.json", RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite,
                System.Array.Empty<string>(), PortabilityClass.ProfileRelative, "sha")
            {
                RestoreTier = RestoreTier.InventoryOnly,
                MigrationMeta = new MigrationRecipeMeta(
                    UiWarning: new LocalizedText("Manual warning", "Manuel uyari"),
                    ManualSteps: System.Array.Empty<string>(),
                    ManualTodo: new[] { "EN: do this by hand. TR: bunu elle yap." },
                    InstallerSource: InstallerSource.Winget,
                    LicenseSource: LicenseSource.AccountLogin,
                    RequiresRelogin: true,
                    BackedUpButNotRestored: true,
                    SurvivesOnOtherDrive: false),
            };

            var store = new MigrationRestoreManifestStore();
            store.Save(dir, new MigrationRestoreManifest(1, new[] { target }));
            MigrationRestoreTarget loaded = Assert.Single(store.Load(dir).Targets);

            Assert.Equal(RestoreTier.InventoryOnly, loaded.RestoreTier);
            Assert.NotNull(loaded.MigrationMeta);
            Assert.True(loaded.MigrationMeta!.RequiresRelogin);
            Assert.Contains("TR:", loaded.MigrationMeta.ManualTodo.Single());
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ManifestBuilder_propagates_recipe_restore_tier_to_target()
    {
        var recipe = new MigrationRecipe(
            SchemaVersion: 3,
            Id: "inventory.app",
            DisplayName: "Inventory App",
            Category: "test",
            Detect: new RecipeDetect(KnownFolder.UserProfile, "prefs.json", Exists: true),
            Items: new[] { new RecipeItem("prefs.json", System.Array.Empty<string>(), System.Array.Empty<string>()) },
            Exclude: System.Array.Empty<string>(),
            SecretRule: "global",
            PortabilityClass: PortabilityClass.ProfileRelative,
            Restore: new RecipeRestore(RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, System.Array.Empty<string>()))
        {
            RestoreTier = RestoreTier.InventoryOnly,
            MigrationMeta = new MigrationRecipeMeta(
                UiWarning: null,
                ManualSteps: System.Array.Empty<string>(),
                ManualTodo: new[] { "Manual step" },
                InstallerSource: null,
                LicenseSource: null,
                RequiresRelogin: false,
                BackedUpButNotRestored: true,
                SurvivesOnOtherDrive: false),
        };
        var meta = new MigrationItemMeta(
            RecipeId: recipe.Id,
            EntryId: "inventory.app#0",
            PortabilityClass: PortabilityClass.ProfileRelative,
            RestoreStrategy: RestoreStrategy.ConfigWrite,
            RestorePhase: RestorePhase.ConfigWrite,
            Preconditions: System.Array.Empty<string>());

        MigrationRestoreTarget? maybeTarget = MigrationRestoreManifestBuilder.BuildTarget(
            recipe, meta, KnownFolder.UserProfile, "prefs.json", "migration/inventory.app/prefs.json", "ABCDEF");
        Assert.NotNull(maybeTarget);
        MigrationRestoreTarget target = maybeTarget!;

        Assert.Equal(RestoreTier.InventoryOnly, target.RestoreTier);
        Assert.Equal("abcdef", target.Sha256);
        Assert.NotNull(target.MigrationMeta);
        Assert.True(target.MigrationMeta!.BackedUpButNotRestored);
    }

    [Theory]
    [InlineData("../../escape.cfg")]   // traversal
    [InlineData(@"C:\abs.cfg")]        // absolute / drive-qualified
    [InlineData("%APPDATA%/x.cfg")]    // env token
    [InlineData("/rooted.cfg")]        // rooted
    public void Load_rejects_an_unsafe_relative_path(string relativePath)
    {
        string dir = TempDir();
        try
        {
            new MigrationRestoreManifestStore().Save(dir, ManifestWith(relativePath));
            Assert.Throws<MigrationManifestException>(() => new MigrationRestoreManifestStore().Load(dir));
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_rejects_a_traversal_in_the_package_relative_source()
    {
        string dir = TempDir();
        try
        {
            new MigrationRestoreManifestStore().Save(dir, ManifestWith(".gitconfig", source: "../outside/f.cfg"));
            Assert.Throws<MigrationManifestException>(() => new MigrationRestoreManifestStore().Load(dir));
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_rejects_an_unknown_schema_version()
    {
        string dir = TempDir();
        try
        {
            new MigrationRestoreManifestStore().Save(dir,
                new MigrationRestoreManifest(99, System.Array.Empty<MigrationRestoreTarget>()));
            Assert.Throws<MigrationManifestException>(() => new MigrationRestoreManifestStore().Load(dir));
        }
        finally { System.IO.Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Zip_export_then_import_preserves_the_package_content()
    {
        string root = TempDir();
        try
        {
            string pkg = Path.Combine(root, "pkg");
            Directory.CreateDirectory(Path.Combine(pkg, "migration", "git.config"));
            File.WriteAllText(Path.Combine(pkg, "migration", "git.config", ".gitconfig"), "[user]\n name = a");
            new MigrationRestoreManifestStore().Save(pkg, ManifestWith(".gitconfig", "migration/git.config/.gitconfig"));

            string zip = Path.Combine(root, "package.zip");
            MigrationPackageArchive.Export(pkg, zip);
            Assert.True(File.Exists(zip));

            string outDir = Path.Combine(root, "imported");
            MigrationPackageArchive.Import(zip, outDir);

            Assert.Equal("[user]\n name = a",
                File.ReadAllText(Path.Combine(outDir, "migration", "git.config", ".gitconfig")));
            // The manifest round-trips through the zip too — restore can consume the imported folder.
            MigrationRestoreManifest loaded = new MigrationRestoreManifestStore().Load(outDir);
            Assert.Single(loaded.Targets);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Import_refuses_a_zip_slip_entry()
    {
        string root = TempDir();
        try
        {
            string zip = Path.Combine(root, "evil.zip");
            using (FileStream fs = File.Create(zip))
            using (var archive = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                System.IO.Compression.ZipArchiveEntry e = archive.CreateEntry("../escaped.txt");
                using StreamWriter w = new(e.Open());
                w.Write("pwned");
            }

            string outDir = Path.Combine(root, "out");
            Assert.Throws<InvalidOperationException>(() => MigrationPackageArchive.Import(zip, outDir));
            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

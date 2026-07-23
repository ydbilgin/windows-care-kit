using WindowsCareKit.Core.Modules.Migration;
using Xunit;
using WindowsCareKit.Tests.TestInfra;

namespace WindowsCareKit.Tests.MigrationRestore;

/// <summary>
/// F5 (manifest validation: traversal/absolute/escape rejection on load).
/// </summary>
public class MigrationManifestTests
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
            var store = new MigrationRestoreManifestStore(new SanctionedFileWriter());
            store.Save(dir, ManifestWith(".gitconfig"));
            MigrationRestoreManifest loaded = store.Load(dir);

            Assert.Equal(1, loaded.SchemaVersion);
            MigrationRestoreTarget t = Assert.Single(loaded.Targets);
            Assert.Equal("git.config", t.RecipeId);
            Assert.Equal(KnownFolder.UserProfile, t.KnownFolder);
            Assert.Equal(".gitconfig", t.RelativePath);
        }
        finally { TestFs.DeleteResilient(dir); }
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

            var store = new MigrationRestoreManifestStore(new SanctionedFileWriter());
            store.Save(dir, new MigrationRestoreManifest(1, new[] { target }));
            MigrationRestoreTarget loaded = Assert.Single(store.Load(dir).Targets);

            Assert.Equal(RestoreTier.InventoryOnly, loaded.RestoreTier);
            Assert.NotNull(loaded.MigrationMeta);
            Assert.True(loaded.MigrationMeta!.RequiresRelogin);
            Assert.Contains("TR:", loaded.MigrationMeta.ManualTodo.Single());
        }
        finally { TestFs.DeleteResilient(dir); }
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
            new MigrationRestoreManifestStore(new SanctionedFileWriter()).Save(dir, ManifestWith(relativePath));
            Assert.Throws<MigrationManifestException>(() => new MigrationRestoreManifestStore(new SanctionedFileWriter()).Load(dir));
        }
        finally { TestFs.DeleteResilient(dir); }
    }

    [Fact]
    public void Load_rejects_a_traversal_in_the_package_relative_source()
    {
        string dir = TempDir();
        try
        {
            new MigrationRestoreManifestStore(new SanctionedFileWriter()).Save(dir, ManifestWith(".gitconfig", source: "../outside/f.cfg"));
            Assert.Throws<MigrationManifestException>(() => new MigrationRestoreManifestStore(new SanctionedFileWriter()).Load(dir));
        }
        finally { TestFs.DeleteResilient(dir); }
    }

    [Fact]
    public void Load_rejects_an_unknown_schema_version()
    {
        string dir = TempDir();
        try
        {
            new MigrationRestoreManifestStore(new SanctionedFileWriter()).Save(dir,
                new MigrationRestoreManifest(99, System.Array.Empty<MigrationRestoreTarget>()));
            Assert.Throws<MigrationManifestException>(() => new MigrationRestoreManifestStore(new SanctionedFileWriter()).Load(dir));
        }
        finally { TestFs.DeleteResilient(dir); }
    }
}

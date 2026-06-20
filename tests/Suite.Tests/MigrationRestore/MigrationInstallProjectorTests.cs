using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using Xunit;

namespace WindowsCareKit.Tests.MigrationRestore;

/// <summary>
/// The pure <see cref="MigrationInstallProjector"/>: it maps recipes that carry a v2 install block into the Kur
/// module's <see cref="InstallEntry"/> shape (ONE per recipe), so the package can self-describe its reinstall
/// plan and restore can feed the EXISTING gated <see cref="InstallPlanner"/>. No IO, no execution.
/// </summary>
public class MigrationInstallProjectorTests
{
    private static MigrationRecipe Recipe(string id, RecipeInstall? install, params string[] itemPaths) => new(
        SchemaVersion: install is null ? 1 : 2, Id: id, DisplayName: $"App {id}", Category: "dev-tools",
        Detect: new RecipeDetect(KnownFolder.UserProfile, "." + id, Exists: true),
        Items: itemPaths.Length == 0
            ? new[] { new RecipeItem("." + id, Array.Empty<string>(), Array.Empty<string>()) }
            : itemPaths.Select(p => new RecipeItem(p, Array.Empty<string>(), Array.Empty<string>())).ToArray(),
        Exclude: Array.Empty<string>(), SecretRule: "global",
        PortabilityClass: PortabilityClass.ProfileRelative,
        Restore: new RecipeRestore(RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, Array.Empty<string>()))
    {
        Install = install,
    };

    [Fact]
    public void Projects_one_entry_per_recipe_that_has_an_install_block()
    {
        var winget = new RecipeInstall(RecipeInstallMethod.Winget, "Git.Git", null, null, RequiresAdmin: true, RebootExpected: false);
        var npm = new RecipeInstall(RecipeInstallMethod.Npm, null, "@anthropic-ai/claude-code", null, false, false);

        MigrationInstallProjection p = MigrationInstallProjector.Project(new[]
        {
            Recipe("git.config", winget),
            Recipe("config.only", install: null),     // no install block → no entry
            Recipe("anthropic.claude-code", npm),
        });

        Assert.Equal(2, p.Entries.Count);
        Assert.Empty(p.Skipped);

        InstallEntry g = p.Entries[0];
        Assert.Equal("migration:git.config:install", g.Id);
        Assert.Equal("install", g.Phase);
        Assert.Equal("dev-tools", g.Category);
        Assert.Equal(InstallMethod.Winget, g.Method);
        Assert.Equal("Git.Git", g.WingetId);
        Assert.Null(g.NpmPackage);
        Assert.True(g.RequiresAdmin);
        Assert.Equal(InstallTier.Auto, g.InstallTier);
        Assert.Equal("App git.config", g.Description);

        InstallEntry c = p.Entries[1];
        Assert.Equal("migration:anthropic.claude-code:install", c.Id);
        Assert.Equal(InstallMethod.Npm, c.Method);
        Assert.Equal("@anthropic-ai/claude-code", c.NpmPackage);
        Assert.Null(c.WingetId);

        // Deterministic restore order follows recipe order (the config-only recipe does not consume a slot).
        Assert.True(g.RestoreOrder < c.RestoreOrder);
    }

    [Fact]
    public void Url_manual_projects_to_a_manual_after_entry_with_the_url()
    {
        var url = new RecipeInstall(RecipeInstallMethod.UrlManual, null, null, "https://nvidia.com/app", false, true);
        MigrationInstallProjection p = MigrationInstallProjector.Project(new[] { Recipe("nvidia.app", url) });

        InstallEntry e = Assert.Single(p.Entries);
        Assert.Equal(InstallMethod.UrlManual, e.Method);
        Assert.Equal(InstallTier.ManualAfter, e.InstallTier);
        Assert.Equal("https://nvidia.com/app", e.ManualUrl);
        Assert.False(e.IsAutomatable); // url-manual is never automatable → never auto-run
    }

    [Fact]
    public void Duplicate_recipe_id_is_skipped_and_surfaced()
    {
        var winget = new RecipeInstall(RecipeInstallMethod.Winget, "Git.Git", null, null, false, false);
        MigrationInstallProjection p = MigrationInstallProjector.Project(new[]
        {
            Recipe("git.config", winget),
            Recipe("git.config", winget), // duplicate recipe id
        });

        Assert.Single(p.Entries);
        Assert.Contains(p.Skipped, s => s.Reason.Contains("duplicate recipe id"));
    }
}

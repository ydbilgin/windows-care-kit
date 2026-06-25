using WindowsCareKit.Core.Modules.Migration;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

public class CommunityRecipeSourceTests
{
    [Fact]
    public void Honest_profile_relative_community_recipe_is_accepted()
    {
        CommunityRecipeLoadResult result = CommunityRecipeSource.LoadCandidates(
            [new CommunityRecipeCandidate("contoso.app.json", Recipe())],
            []);

        MigrationRecipe recipe = Assert.Single(result.Loaded);
        Assert.Empty(result.Rejected);
        Assert.Equal("contoso.app", recipe.Id);
        Assert.Equal(CatalogTier.Community, recipe.CatalogTier);
        Assert.Equal(RestoreTier.ConfigCopy, recipe.RestoreTier);
        Assert.Equal(UpstreamDataLicense.CcBy, recipe.UpstreamDataLicense);
    }

    [Fact]
    public void Over_claiming_community_recipe_is_rejected_with_gate_reason()
    {
        CommunityRecipeLoadResult result = CommunityRecipeSource.LoadCandidates(
            [new CommunityRecipeCandidate("locked.app.json", Recipe(
                id: "locked.app",
                portabilityClass: "machine-locked",
                restoreTier: "config-copy"))],
            []);

        Assert.Empty(result.Loaded);
        CommunityRecipeRejection rejection = Assert.Single(result.Rejected);
        Assert.Equal("locked.app", rejection.Id);
        Assert.Contains("portability", rejection.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MachineLocked", rejection.Reason);
    }

    [Fact]
    public void Self_declared_trusted_community_recipe_is_forced_to_community()
    {
        CommunityRecipeLoadResult result = CommunityRecipeSource.LoadCandidates(
            [new CommunityRecipeCandidate("self.trusted.json", Recipe(id: "self.trusted", catalogTier: "trusted"))],
            []);

        MigrationRecipe recipe = Assert.Single(result.Loaded);
        Assert.Empty(result.Rejected);
        Assert.Equal(CatalogTier.Community, recipe.CatalogTier);
    }

    [Fact]
    public void Builtin_id_collision_rejects_community_recipe()
    {
        IReadOnlyList<MigrationRecipe> trusted = BuiltinRecipeSource.LoadAll();
        CommunityRecipeLoadResult result = CommunityRecipeSource.LoadCandidates(
            [new CommunityRecipeCandidate("git.config.json", Recipe(id: "git.config"))],
            trusted.Select(r => r.Id));

        Assert.Empty(result.Loaded);
        CommunityRecipeRejection rejection = Assert.Single(result.Rejected);
        Assert.Equal("git.config", rejection.Id);
        Assert.Contains("collides", rejection.Reason);
        Assert.Contains("trusted", rejection.Reason);
    }

    [Fact]
    public void Schema_invalid_file_is_rejected_without_blocking_same_pack()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wck-community-recipes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(Path.Combine(dir, "bad.recipe.json"), Recipe(id: "bad.recipe", extraRootField: "\"unknownRoot\": true,"));
            File.WriteAllText(Path.Combine(dir, "good.recipe.json"), Recipe(id: "good.recipe"));

            CommunityRecipeLoadResult result = CommunityRecipeSource.LoadFromDirectory(dir, []);

            MigrationRecipe recipe = Assert.Single(result.Loaded);
            Assert.Equal("good.recipe", recipe.Id);
            CommunityRecipeRejection rejection = Assert.Single(result.Rejected);
            Assert.Equal("bad.recipe", rejection.Id);
            Assert.Contains("unknownRoot", rejection.Reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Combined_view_keeps_trusted_and_community_tiers_explicit()
    {
        MigrationRecipe trusted = MigrationRecipeLoader.Load(Recipe(id: "trusted.app", catalogTier: "trusted"));
        MigrationRecipe community = MigrationRecipeLoader.Load(Recipe(id: "community.app", catalogTier: "trusted"));

        IReadOnlyList<MigrationRecipe> combined = CommunityRecipeSource.CombineTrustedAndCommunity(
            [trusted with { CatalogTier = CatalogTier.Community }],
            [community]);

        Assert.Equal(CatalogTier.Community, combined.Single(r => r.Id == "community.app").CatalogTier);
        Assert.Equal(CatalogTier.Trusted, combined.Single(r => r.Id == "trusted.app").CatalogTier);
    }

    private static string Recipe(
        string id = "contoso.app",
        string catalogTier = "community",
        string knownFolder = "AppData",
        string portabilityClass = "profile-relative",
        string restoreTier = "config-copy",
        string extraRootField = "")
        => $$"""
        {
          "schemaVersion": 3,
          "id": "{{id}}",
          "displayName": "Contoso App",
          "category": "dev-tools",
          {{extraRootField}}
          "detect": { "knownFolder": "{{knownFolder}}", "path": "Contoso/App", "exists": true },
          "items": [
            { "path": "Contoso/App/settings.json", "include": ["settings.json"], "exclude": [] }
          ],
          "exclude": [],
          "secretRule": "global",
          "portabilityClass": "{{portabilityClass}}",
          "restore": { "strategy": "config-write", "phase": "configWrite", "preconditions": [] },
          "restoreTier": "{{restoreTier}}",
          "catalogTier": "{{catalogTier}}",
          "upstreamDataLicense": "cc-by"
        }
        """;
}

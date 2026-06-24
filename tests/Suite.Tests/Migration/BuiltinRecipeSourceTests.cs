using WindowsCareKit.Core.Modules.Migration;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

/// <summary>C: the embedded seed recipes load + strictly validate; the Slice-1 set is exactly the agreed apps.</summary>
public class BuiltinRecipeSourceTests
{
    [Fact]
    public void All_seed_recipes_load_and_validate()
    {
        IReadOnlyList<MigrationRecipe> recipes = BuiltinRecipeSource.LoadAll();
        Assert.NotEmpty(recipes);
        Assert.All(recipes, r =>
        {
            Assert.Equal(3, r.SchemaVersion);
            Assert.NotEmpty(r.Id);
            Assert.NotEmpty(r.Items);
            Assert.NotNull(r.MigrationMeta?.UiWarning);
        });
    }

    [Fact]
    public void Slice1_seed_set_contains_required_common_apps()
    {
        var ids = BuiltinRecipeSource.LoadAll().Select(r => r.Id).ToHashSet();
        Assert.Contains("anthropic.claude-code", ids);
        Assert.Contains("discord", ids);
        Assert.Contains("microsoft.vscode", ids);
        Assert.Contains("git.config", ids);
        Assert.Contains("google.chrome", ids);
        Assert.Contains("microsoft.edge", ids);
        Assert.Contains("valve.steam", ids);
        Assert.Contains("7zip.7zip", ids);
        Assert.Contains("notepadplusplus.notepadplusplus", ids);
        Assert.Contains("telegram.telegramdesktop", ids);
        Assert.Contains("qbittorrent.qbittorrent", ids);
        Assert.Contains("obsproject.obsstudio", ids);
        Assert.Contains("spotify.spotify", ids);
        Assert.Contains("libreoffice.libreoffice", ids);
    }

    [Fact]
    public void Ssh_recipe_is_deferred_not_shipped()
    {
        var ids = BuiltinRecipeSource.LoadAll().Select(r => r.Id).ToList();
        Assert.DoesNotContain(ids, id => id.Contains("ssh", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Claude_recipe_excludes_cache_shell_snapshots_and_todos()
    {
        MigrationRecipe claude = BuiltinRecipeSource.LoadAll().Single(r => r.Id == "anthropic.claude-code");
        Assert.Contains(claude.Exclude, e => e.Contains("shell-snapshots"));
        Assert.Contains(claude.Exclude, e => e.Contains("todos"));
        Assert.Contains(claude.Exclude, e => e.Contains("cache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Discord_excludes_cache_and_blob_storage()
    {
        MigrationRecipe discord = BuiltinRecipeSource.LoadAll().Single(r => r.Id == "discord");
        Assert.Contains(discord.Exclude, e => e.Contains("Cache", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(discord.Exclude, e => e.Contains("blob_storage"));
    }

    [Fact]
    public void Loading_is_deterministically_ordered_by_id()
    {
        var ids = BuiltinRecipeSource.LoadAll().Select(r => r.Id).ToList();
        var sorted = ids.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, ids);
    }

    [Fact]
    public void Machine_locked_builtin_recipes_are_inventory_only_and_never_claim_works()
    {
        var recipes = BuiltinRecipeSource.LoadAll()
            .Where(r => r.PortabilityClass == PortabilityClass.MachineLocked)
            .ToList();

        Assert.NotEmpty(recipes);
        Assert.All(recipes, r =>
        {
            Assert.Equal(RestoreTier.InventoryOnly, r.RestoreTier);
            Assert.False(PortabilityBadge.Compute(r.PortabilityClass, hasPreconditions: false).MayClaimWorks);
        });
    }

    [Fact]
    public void Steam_machine_root_is_inventory_only()
    {
        MigrationRecipe steam = BuiltinRecipeSource.LoadAll().Single(r => r.Id == "valve.steam");
        Assert.Equal(RestoreTier.InventoryOnly, steam.RestoreTier);
        RecipeItem item = Assert.Single(steam.Items);
        Assert.Equal(RecipeItemKind.MachineRoot, item.Kind);
        Assert.Equal("steam", item.LibraryDetector);
    }
}

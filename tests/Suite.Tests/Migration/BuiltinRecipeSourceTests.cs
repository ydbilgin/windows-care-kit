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
            Assert.Equal(1, r.SchemaVersion);
            Assert.NotEmpty(r.Id);
            Assert.NotEmpty(r.Items);
        });
    }

    [Fact]
    public void Slice1_seed_set_is_claude_discord_vscode_git()
    {
        var ids = BuiltinRecipeSource.LoadAll().Select(r => r.Id).ToHashSet();
        Assert.Contains("anthropic.claude-code", ids);
        Assert.Contains("discord", ids);
        Assert.Contains("microsoft.vscode", ids);
        Assert.Contains("git.config", ids);
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
}

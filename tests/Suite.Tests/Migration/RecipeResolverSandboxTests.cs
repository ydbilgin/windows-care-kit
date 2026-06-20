using WindowsCareKit.Core.Modules.Migration;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

/// <summary>F2: the resolver is a REAL sandbox — detect → token-expand → canonicalize → profile-root containment.</summary>
public class RecipeResolverSandboxTests
{
    private static MigrationRecipe Recipe(
        KnownFolder kf, string detectPath, bool detectExists, params RecipeItem[] items)
        => new(
            SchemaVersion: 1, Id: "test.app", DisplayName: "Test", Category: "cat",
            Detect: new RecipeDetect(kf, detectPath, detectExists),
            Items: items,
            Exclude: Array.Empty<string>(),
            SecretRule: "global",
            PortabilityClass: PortabilityClass.ProfileRelative,
            Restore: new RecipeRestore(RestoreStrategy.MergeAfterInstall, RestorePhase.ConfigWrite, Array.Empty<string>()));

    private static RecipeItem Item(string path) => new(path, Array.Empty<string>(), Array.Empty<string>());

    [Fact]
    public void Detect_absent_skips_the_whole_recipe()
    {
        var fs = new FakeRecipeFileSystem(); // nothing exists
        var recipe = Recipe(KnownFolder.UserProfile, ".claude", detectExists: true, Item(".claude/CLAUDE.md"));

        ResolvedRecipe result = MigrationTestData.Resolver(fs).Resolve(recipe);

        Assert.False(result.DetectMatched);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void Detect_present_resolves_existing_items()
    {
        var fs = new FakeRecipeFileSystem()
            .AddDir(@"C:\Users\alice\.claude")
            .AddFile(@"C:\Users\alice\.claude\CLAUDE.md");
        var recipe = Recipe(KnownFolder.UserProfile, ".claude", true, Item(".claude/CLAUDE.md"));

        ResolvedRecipe result = MigrationTestData.Resolver(fs).Resolve(recipe);

        Assert.True(result.DetectMatched);
        ResolvedRecipeItem item = Assert.Single(result.Items);
        Assert.Equal(@"C:\Users\alice\.claude\CLAUDE.md", item.AbsoluteSource);
        Assert.Equal("migration/test.app/.claude/CLAUDE.md", item.TargetRelative);
    }

    [Fact]
    public void Missing_item_under_present_detect_is_skipped_not_emitted()
    {
        var fs = new FakeRecipeFileSystem().AddDir(@"C:\Users\alice\.claude"); // detect ok, item file absent
        var recipe = Recipe(KnownFolder.UserProfile, ".claude", true, Item(".claude/CLAUDE.md"));

        ResolvedRecipe result = MigrationTestData.Resolver(fs).Resolve(recipe);

        Assert.True(result.DetectMatched);
        Assert.Empty(result.Items);
        Assert.Contains(result.Skipped, s => s.Reason.Contains("does not exist"));
    }

    [Fact]
    public void Traversal_item_is_rejected_and_emits_no_entry()
    {
        var fs = new FakeRecipeFileSystem().AddDir(@"C:\Users\alice\.claude");
        var recipe = Recipe(KnownFolder.UserProfile, ".claude", true, Item("../bob/secrets"));

        ResolvedRecipe result = MigrationTestData.Resolver(fs).Resolve(recipe);

        Assert.Empty(result.Items);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void Unresolved_reparse_point_source_fails_closed()
    {
        var fs = new FakeRecipeFileSystem()
            .AddDir(@"C:\Users\alice\.claude")
            .AddReparse(@"C:\Users\alice\.claude\link", target: null); // junction we can't resolve
        var recipe = Recipe(KnownFolder.UserProfile, ".claude", true, Item(".claude/link"));

        ResolvedRecipe result = MigrationTestData.Resolver(fs).Resolve(recipe);

        Assert.Empty(result.Items);
        Assert.Contains(result.Skipped, s => s.Reason.Contains("reparse"));
    }

    [Fact]
    public void Reparse_that_canonicalizes_out_of_the_profile_is_refused()
    {
        // Lexical path is inside the profile, but the junction really points to another user's tree.
        var fs = new FakeRecipeFileSystem()
            .AddDir(@"C:\Users\alice\.claude")
            .AddReparse(@"C:\Users\alice\.claude\evil", target: @"C:\Users\bob\secrets");
        var recipe = Recipe(KnownFolder.UserProfile, ".claude", true, Item(".claude/evil"));

        ResolvedRecipe result = MigrationTestData.Resolver(fs).Resolve(recipe);

        Assert.Empty(result.Items);
        Assert.Contains(result.Skipped, s => s.Reason.Contains("escapes"));
    }

    [Fact]
    public void Reparse_that_stays_inside_the_profile_is_allowed()
    {
        var fs = new FakeRecipeFileSystem()
            .AddDir(@"C:\Users\alice\.claude")
            .AddReparse(@"C:\Users\alice\.claude\real", target: @"C:\Users\alice\.claude\actual");
        var recipe = Recipe(KnownFolder.UserProfile, ".claude", true, Item(".claude/real"));

        ResolvedRecipe result = MigrationTestData.Resolver(fs).Resolve(recipe);

        ResolvedRecipeItem item = Assert.Single(result.Items);
        Assert.Equal(@"C:\Users\alice\.claude\actual", item.AbsoluteSource);
    }

    [Fact]
    public void Resolution_is_deterministic_for_the_same_inputs()
    {
        var fs = new FakeRecipeFileSystem()
            .AddDir(@"C:\Users\alice\.claude")
            .AddFile(@"C:\Users\alice\.claude\a.txt")
            .AddFile(@"C:\Users\alice\.claude\b.txt");
        var recipe = Recipe(KnownFolder.UserProfile, ".claude", true,
            Item(".claude/a.txt"), Item(".claude/b.txt"));

        var r1 = MigrationTestData.Resolver(fs).Resolve(recipe);
        var r2 = MigrationTestData.Resolver(fs).Resolve(recipe);

        Assert.Equal(
            r1.Items.Select(i => i.AbsoluteSource),
            r2.Items.Select(i => i.AbsoluteSource));
        Assert.Equal(2, r1.Items.Count);
    }
}

using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

/// <summary>The bridge: only sandbox-passing items become BackupEntry; secret-glob overlay is layered on (F2/F3/F4).</summary>
public class RecipeToBackupEntryTests
{
    private static MigrationRecipe Recipe(PortabilityClass cls, params RecipeItem[] items)
        => new(
            1, "anthropic.claude-code", "Claude Code", "dev-tools",
            new RecipeDetect(KnownFolder.UserProfile, ".claude", true),
            items, Array.Empty<string>(), "global", cls,
            new RecipeRestore(RestoreStrategy.MergeAfterInstall, RestorePhase.ConfigWrite, new[] { "process-closed" }));

    private static RecipeItem Item(string path, string[]? include = null, string[]? exclude = null)
        => new(path, include ?? Array.Empty<string>(), exclude ?? Array.Empty<string>());

    private static FakeRecipeFileSystem Fs() => new FakeRecipeFileSystem()
        .AddDir(@"C:\Users\alice\.claude")
        .AddDir(@"C:\Users\alice\.claude\projects")
        .AddFile(@"C:\Users\alice\.claude\CLAUDE.md");

    [Fact]
    public void Bridges_only_sandbox_passing_items_into_entries()
    {
        var recipe = Recipe(PortabilityClass.ProfileRelative,
            Item(".claude/CLAUDE.md"),     // exists → bridged
            Item(".claude/missing.json"),  // absent → skipped, no entry
            Item("../escape"));            // traversal → skipped, no entry

        ResolvedRecipe resolved = MigrationTestData.Resolver(Fs()).Resolve(recipe);
        IReadOnlyList<BridgedMigrationItem> bridged = RecipeToBackupEntry.Bridge(resolved);

        BridgedMigrationItem only = Assert.Single(bridged);
        Assert.Equal(@"C:\Users\alice\.claude\CLAUDE.md", only.Entry.Source);
        Assert.True(only.Entry.IsCopyable);
    }

    [Fact]
    public void Carries_recipe_include_through_to_the_entry()
    {
        var recipe = Recipe(PortabilityClass.ProfileRelative,
            Item(".claude/projects", include: new[] { "**/memory/**" }));

        var bridged = RecipeToBackupEntry.Bridge(MigrationTestData.Resolver(Fs()).Resolve(recipe));

        Assert.Equal(new[] { "**/memory/**" }, Assert.Single(bridged).Entry.Include);
    }

    [Fact]
    public void Layers_secret_glob_overlay_into_every_entry_exclude()
    {
        var recipe = Recipe(PortabilityClass.ProfileRelative,
            Item(".claude/projects", exclude: new[] { "**/cache/**" }));

        BackupEntry entry = Assert.Single(RecipeToBackupEntry.Bridge(MigrationTestData.Resolver(Fs()).Resolve(recipe))).Entry;

        Assert.Contains("**/cache/**", entry.Exclude);    // recipe's own
        foreach (string glob in SecretGlobOverlay.Globs)  // overlay on top
            Assert.Contains(glob, entry.Exclude);
    }

    [Fact]
    public void Machine_locked_recipe_gets_a_warning_never_a_works_claim()
    {
        var recipe = Recipe(PortabilityClass.MachineLocked, Item(".claude/CLAUDE.md"));

        var bridged = Assert.Single(RecipeToBackupEntry.Bridge(MigrationTestData.Resolver(Fs()).Resolve(recipe)));

        Assert.NotNull(bridged.Entry.UiWarning);
        Assert.Equal(PortabilityClass.MachineLocked, bridged.Meta.PortabilityClass);
        Assert.False(PortabilityBadge.Compute(bridged.Meta).MayClaimWorks);
    }

    [Fact]
    public void Profile_relative_recipe_has_no_warning()
    {
        var recipe = Recipe(PortabilityClass.ProfileRelative, Item(".claude/CLAUDE.md"));
        var bridged = Assert.Single(RecipeToBackupEntry.Bridge(MigrationTestData.Resolver(Fs()).Resolve(recipe)));
        Assert.Null(bridged.Entry.UiWarning);
    }

    [Fact]
    public void Bridged_entries_project_into_a_gate_approved_plan_deterministically()
    {
        var recipe = Recipe(PortabilityClass.ProfileRelative, Item(".claude/CLAUDE.md"));
        var bridged = RecipeToBackupEntry.Bridge(MigrationTestData.Resolver(Fs()).Resolve(recipe));
        var manifest = new BackupManifest(bridged.Select(b => b.Entry).ToArray());

        string payload = System.IO.Path.Combine(@"C:\Users\alice\wck-backup", "out");
        var planner = new BackupPlanner(TestData.Gate(), new FakeEnvironmentExpander());
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        BackupPlanResult r1 = planner.BuildPlan(manifest, payload, t0);
        BackupPlanResult r2 = planner.BuildPlan(manifest, payload, t0.AddHours(3));

        CopyAction copy = Assert.IsType<CopyAction>(Assert.Single(r1.Plan.Actions));
        Assert.Equal(@"C:\Users\alice\.claude\CLAUDE.md", copy.Source);
        Assert.True(TestData.Gate().Evaluate(copy).Allowed);
        Assert.Equal(r1.Plan.ComputeHash(), r2.Plan.ComputeHash()); // deterministic projection
    }

    [Fact]
    public void Empty_when_detect_did_not_match()
    {
        var recipe = Recipe(PortabilityClass.ProfileRelative, Item(".claude/CLAUDE.md"));
        var fs = new FakeRecipeFileSystem(); // detect absent
        Assert.Empty(RecipeToBackupEntry.Bridge(MigrationTestData.Resolver(fs).Resolve(recipe)));
    }

    // --- B-1: a recipe that over-declares a secret leaf in `include` can never render a green badge ---

    [Fact]
    public void Over_declared_secret_include_sets_the_meta_signal_and_blocks_a_works_badge()
    {
        // ProfileRelative recipe whose include names a secret pattern (id_rsa) — the engine still prunes it,
        // but the badge must NOT claim "works". Non-vacuous: revert the bridge aggregation and this fails.
        var recipe = Recipe(PortabilityClass.ProfileRelative,
            Item(".claude/projects", include: new[] { "id_rsa" }));

        var bridged = Assert.Single(RecipeToBackupEntry.Bridge(MigrationTestData.Resolver(Fs()).Resolve(recipe)));

        Assert.True(bridged.Meta.HasExcludedSecret);
        Assert.False(PortabilityBadge.Compute(bridged.Meta).MayClaimWorks);
    }

    [Fact]
    public void Clean_include_leaves_the_secret_signal_unset_and_the_badge_green()
    {
        // Counter-test (no over-block): a non-secret include keeps the honest ✅ for a clean profile-relative item.
        var recipe = Recipe(PortabilityClass.ProfileRelative,
            Item(".claude/projects", include: new[] { "**/memory/**" }));

        var bridged = Assert.Single(RecipeToBackupEntry.Bridge(MigrationTestData.Resolver(Fs()).Resolve(recipe)));

        Assert.False(bridged.Meta.HasExcludedSecret);
        Assert.True(PortabilityBadge.Compute(bridged.Meta).MayClaimWorks);
    }

    [Fact]
    public void Over_declared_fixed_credential_include_blocks_a_works_badge()
    {
        // Review cx#1: a FIXED credential leaf (logins.json) is NOT a glob-overlay match but IS pruned by the
        // copy engine — the badge must still downgrade. Non-vacuous: the old overlay-only aggregation missed this.
        var recipe = Recipe(PortabilityClass.ProfileRelative,
            Item(".claude/projects", include: new[] { "logins.json" }));

        var bridged = Assert.Single(RecipeToBackupEntry.Bridge(MigrationTestData.Resolver(Fs()).Resolve(recipe)));

        Assert.True(bridged.Meta.HasExcludedSecret);
        Assert.False(PortabilityBadge.Compute(bridged.Meta).MayClaimWorks);
    }

    [Fact]
    public void Path_shaped_secret_include_is_matched_by_its_leaf()
    {
        // Review cx#2: a path-bearing include (**/id_rsa) is reduced to its leaf before the name policy runs.
        var recipe = Recipe(PortabilityClass.ProfileRelative,
            Item(".claude/projects", include: new[] { "**/id_rsa" }));

        var bridged = Assert.Single(RecipeToBackupEntry.Bridge(MigrationTestData.Resolver(Fs()).Resolve(recipe)));

        Assert.True(bridged.Meta.HasExcludedSecret);
    }

    [Fact]
    public void Item_whose_path_leaf_is_a_secret_blocks_a_works_badge()
    {
        // Review cx#2: the declared item path can itself be the secret (empty include) — its leaf must be checked.
        var fs = new FakeRecipeFileSystem()
            .AddDir(@"C:\Users\alice\.claude")
            .AddFile(@"C:\Users\alice\.claude\id_rsa");
        var recipe = Recipe(PortabilityClass.ProfileRelative, Item(".claude/id_rsa"));

        var bridged = Assert.Single(RecipeToBackupEntry.Bridge(MigrationTestData.Resolver(fs).Resolve(recipe)));

        Assert.True(bridged.Meta.HasExcludedSecret);
        Assert.False(PortabilityBadge.Compute(bridged.Meta).MayClaimWorks);
    }
}

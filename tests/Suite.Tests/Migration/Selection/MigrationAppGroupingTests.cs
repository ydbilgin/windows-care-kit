using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Selection;
using Xunit;

namespace WindowsCareKit.Tests.Migration.Selection;

/// <summary>
/// PR-1 grouping behavior (Fable design §A): <see cref="MigrationSelectionBuilder"/> groups candidates by
/// <c>Meta.RecipeId</c> into <see cref="MigrationAppGroup"/>, and <see cref="MigrationBadgeAggregator"/> computes
/// the worst-of app badge as a pure function. Table-driven and non-vacuous per the PR-1 acceptance criteria.
/// </summary>
public sealed class MigrationAppGroupingTests
{
    // ---- A2: grouping by recipe id -----------------------------------------------------------

    [Fact]
    public void Multi_item_candidates_sharing_a_recipe_id_become_one_app_with_all_parts()
    {
        MigrationSelectionCandidate claudeMd = Candidate("anthropic.claude-code#0", "anthropic.claude-code", "Claude Code", "dev-tools");
        MigrationSelectionCandidate settings = Candidate("anthropic.claude-code#1", "anthropic.claude-code", "Claude Code", "dev-tools");
        MigrationSelectionCandidate projects = Candidate("anthropic.claude-code#2", "anthropic.claude-code", "Claude Code", "dev-tools");

        MigrationSelectionGroup group = MigrationSelectionBuilder.Build([claudeMd, settings, projects])
            .Single(g => g.Category == MigrationCategory.DevConfigEditors);

        MigrationAppGroup app = Assert.Single(group.Apps);
        Assert.Equal("anthropic.claude-code", app.RecipeId);
        Assert.True(app.HasMultipleParts);
        Assert.Equal(3, app.Parts.Count);
        Assert.Equal(1, group.AppCount);
    }

    [Fact]
    public void Fallback_single_candidate_is_a_single_part_app_with_no_expander()
    {
        MigrationSelectionCandidate fallback = Candidate("git.config#present", "git.config", "Git config", "dev-tools");

        MigrationAppGroup app = MigrationSelectionBuilder.Build([fallback])
            .Single(g => g.Category == MigrationCategory.DevConfigEditors)
            .Apps.Single();

        Assert.False(app.HasMultipleParts);
        Assert.Single(app.Parts);
    }

    [Fact]
    public void Different_recipe_ids_in_the_same_category_stay_separate_apps()
    {
        MigrationSelectionCandidate vscode = Candidate("microsoft.vscode#0", "microsoft.vscode", "VS Code", "dev-tools");
        MigrationSelectionCandidate node = Candidate("openjs.nodejs.lts#0", "openjs.nodejs.lts", "Node.js LTS", "dev-tools");

        MigrationSelectionGroup group = MigrationSelectionBuilder.Build([vscode, node])
            .Single(g => g.Category == MigrationCategory.DevConfigEditors);

        Assert.Equal(2, group.AppCount);
        Assert.Equal(["microsoft.vscode", "openjs.nodejs.lts"], group.Apps.Select(a => a.RecipeId).OrderBy(id => id, StringComparer.Ordinal));
    }

    // ---- A3/A6: forced-app + count semantics --------------------------------------------------

    [Fact]
    public void An_app_is_forced_the_moment_any_part_is_forced()
    {
        MigrationSelectionCandidate portable = Candidate("app#0", "app", "App", "personal") with
        {
            CloudBackup = CloudBackupStatus.BackedUp, IsOnSystemDrive = false, IsUnique = false, IsRegenerable = true,
        };
        MigrationSelectionCandidate forced = Candidate("app#1", "app", "App", "personal") with
        {
            OneDriveRedirectedSyncOff = true,
            Meta = Meta("app", "app#1", PortabilityClass.MachineLocked),
        };

        MigrationAppGroup app = MigrationSelectionBuilder.Build([portable, forced])
            .Single(g => g.Category == MigrationCategory.IrreplaceablePersonal)
            .Apps.Single();

        Assert.True(app.IsForced);
        Assert.True(app.IsEffectivelySelected);
        // A forced app is an application-level invariant: clearing it leaves every part selected, never half-on.
        app.SetAppSelected(false);
        Assert.True(app.Parts.Single(p => p.Candidate.Id == "app#1").IsSelected);
        Assert.True(app.Parts.Single(p => p.Candidate.Id == "app#0").IsSelected);
    }

    [Fact]
    public void An_app_with_no_forced_or_selected_parts_is_not_effectively_selected()
    {
        MigrationSelectionCandidate a = Candidate("app#0", "app", "App", "games") with
        {
            CloudBackup = CloudBackupStatus.BackedUp, IsOnSystemDrive = false, IsUnique = false, IsRegenerable = true,
        };
        MigrationSelectionCandidate b = Candidate("app#1", "app", "App", "games") with
        {
            CloudBackup = CloudBackupStatus.BackedUp, IsOnSystemDrive = false, IsUnique = false, IsRegenerable = true,
        };

        MigrationAppGroup app = MigrationSelectionBuilder.Build([a, b])
            .Single(g => g.Category == MigrationCategory.GameSaves)
            .Apps.Single();

        Assert.False(app.IsForced);
        Assert.False(app.IsEffectivelySelected);
    }

    [Fact]
    public void App_checkbox_cascades_to_every_part_when_toggled_on_and_off()
    {
        MigrationSelectionCandidate a = Candidate("app#0", "app", "App", "games");
        MigrationSelectionCandidate b = Candidate("app#1", "app", "App", "games") with
        {
            CloudBackup = CloudBackupStatus.BackedUp, IsOnSystemDrive = false, IsUnique = false, IsRegenerable = true,
        };

        MigrationAppGroup app = MigrationSelectionBuilder.Build([a, b])
            .Single(g => g.Category == MigrationCategory.GameSaves)
            .Apps.Single();

        app.SetAppSelected(true);
        Assert.All(app.Parts, part => Assert.True(part.IsSelected));

        app.SetAppSelected(false);
        Assert.All(app.Parts, part => Assert.False(part.IsSelected));
    }

    [Fact]
    public void App_smart_default_is_on_if_any_part_recommends_selection()
    {
        // "app#0" alone scores 3 (top irreplaceability tier, On); "app#1" scores 0 (Off). The app-level
        // recommended default is the OR across parts (A3), not each part's own independent default.
        MigrationSelectionCandidate top = Candidate("app#0", "app", "App", "personal");
        MigrationSelectionCandidate low = Candidate("app#1", "app", "App", "personal") with
        {
            CloudBackup = CloudBackupStatus.BackedUp, IsOnSystemDrive = false, IsUnique = false, IsRegenerable = true,
        };

        MigrationAppGroup app = MigrationSelectionBuilder.Build([top, low])
            .Single(g => g.Category == MigrationCategory.IrreplaceablePersonal)
            .Apps.Single();

        Assert.True(app.SmartDefaultOn);
        Assert.All(app.Parts, part => Assert.True(part.IsSelected));
    }

    [Fact]
    public void Category_and_app_counts_reflect_apps_not_files()
    {
        MigrationSelectionCandidate a0 = Candidate("app#0", "app", "App", "games");
        MigrationSelectionCandidate a1 = Candidate("app#1", "app", "App", "games");
        MigrationSelectionCandidate b0 = Candidate("other#0", "other", "Other", "games") with
        {
            CloudBackup = CloudBackupStatus.BackedUp, IsOnSystemDrive = false, IsUnique = false, IsRegenerable = true,
        };

        MigrationSelectionGroup group = MigrationSelectionBuilder.Build([a0, a1, b0])
            .Single(g => g.Category == MigrationCategory.GameSaves);

        // 3 files, but 2 apps — A6: the header/footer count what the user sees (rows), not files.
        Assert.Equal(3, group.Items.Count);
        Assert.Equal(2, group.AppCount);
        Assert.Equal(1, group.SelectedAppCount); // "app" is effectively selected (both parts On); "other" is Off
    }

    // ---- A5: worst-of badge aggregation (table-driven) ----------------------------------------

    public static IEnumerable<object[]> AggregationCases()
    {
        // (parts kinds, expected worst kind, expected MayClaimWorks)
        yield return
        [
            new[] { BadgeKind.PortableClean, BadgeKind.PortableClean }, BadgeKind.PortableClean, true,
        ];
        yield return
        [
            new[] { BadgeKind.PortableClean, BadgeKind.PortableWithStep }, BadgeKind.PortableWithStep, true,
        ];
        yield return
        [
            new[] { BadgeKind.PortableClean, BadgeKind.Partial }, BadgeKind.Partial, false,
        ];
        yield return
        [
            new[] { BadgeKind.PortableWithStep, BadgeKind.MachineLocked }, BadgeKind.MachineLocked, false,
        ];
        yield return
        [
            new[] { BadgeKind.Partial, BadgeKind.MachineLocked }, BadgeKind.MachineLocked, false,
        ];
        yield return
        [
            new[] { BadgeKind.PortableClean, BadgeKind.PortableWithStep, BadgeKind.Partial, BadgeKind.MachineLocked },
            BadgeKind.MachineLocked, false,
        ];
    }

    [Theory]
    [MemberData(nameof(AggregationCases))]
    public void Aggregate_picks_the_worst_kind_and_ands_may_claim_works(
        BadgeKind[] kinds, BadgeKind expectedWorst, bool expectedMayClaimWorks)
    {
        MigrationBadgePresentation[] parts = kinds.Select(Badge).ToArray();

        MigrationBadgePresentation aggregated = MigrationBadgeAggregator.Aggregate(parts);

        Assert.Equal(expectedWorst, aggregated.DisplayKind);
        Assert.Equal(expectedMayClaimWorks, aggregated.MayClaimWorks);
    }

    [Fact]
    public void Aggregate_never_upgrades_a_single_bad_part_even_among_many_good_ones()
    {
        MigrationBadgePresentation[] parts =
        [
            Badge(BadgeKind.PortableClean), Badge(BadgeKind.PortableClean),
            Badge(BadgeKind.PortableClean), Badge(BadgeKind.MachineLocked),
        ];

        MigrationBadgePresentation aggregated = MigrationBadgeAggregator.Aggregate(parts);

        Assert.Equal(BadgeKind.MachineLocked, aggregated.DisplayKind);
        Assert.False(aggregated.MayClaimWorks);
    }

    [Fact]
    public void Aggregate_ors_the_secret_overlay_across_parts()
    {
        MigrationBadgePresentation[] parts =
        [
            Badge(BadgeKind.PortableClean) with { HasSecretOverlay = false },
            Badge(BadgeKind.PortableClean) with { HasSecretOverlay = true },
        ];

        MigrationBadgePresentation aggregated = MigrationBadgeAggregator.Aggregate(parts);

        Assert.True(aggregated.HasSecretOverlay);
    }

    [Fact]
    public void Aggregate_of_a_single_part_is_that_parts_own_presentation_unchanged()
    {
        MigrationBadgePresentation only = Badge(BadgeKind.PortableWithStep);

        MigrationBadgePresentation aggregated = MigrationBadgeAggregator.Aggregate([only]);

        Assert.Equal(only, aggregated);
    }

    [Fact]
    public void Breakdown_is_null_when_every_part_shares_the_same_kind()
    {
        MigrationBadgePresentation[] parts = [Badge(BadgeKind.PortableClean), Badge(BadgeKind.PortableClean)];

        Assert.Null(MigrationBadgeAggregator.Breakdown(parts));
    }

    [Fact]
    public void Breakdown_is_null_for_a_single_part()
    {
        Assert.Null(MigrationBadgeAggregator.Breakdown([Badge(BadgeKind.PortableClean)]));
    }

    [Fact]
    public void Breakdown_counts_the_best_kind_out_of_the_total_when_parts_differ()
    {
        // 3 portable + 1 machine-locked — the Claude Code A4 example ("3/4 taşınabilir").
        MigrationBadgePresentation[] parts =
        [
            Badge(BadgeKind.PortableClean), Badge(BadgeKind.PortableClean),
            Badge(BadgeKind.PortableClean), Badge(BadgeKind.MachineLocked),
        ];

        BadgeBreakdown? breakdown = MigrationBadgeAggregator.Breakdown(parts);

        Assert.NotNull(breakdown);
        Assert.Equal(3, breakdown!.BestCount);
        Assert.Equal(4, breakdown.Total);
        Assert.Equal("taşınabilir", breakdown.LabelTr);
        Assert.Equal("portable", breakdown.LabelEn);
    }

    [Fact]
    public void Equal_severity_badges_always_prefer_the_restore_cap_disclosure_regardless_of_part_order()
    {
        MigrationBadgePresentation unanalyzed = Badge(BadgeKind.Partial) with
        {
            LabelTr = "incelenemedi",
            LabelEn = "not analyzed",
        };
        MigrationBadgePresentation capped = Badge(BadgeKind.Partial) with
        {
            IsRestoreTierCapped = true,
            LabelTr = "yalnız envanter / manuel",
            LabelEn = "inventory / manual only",
        };

        MigrationBadgePresentation forward = MigrationBadgeAggregator.Aggregate([unanalyzed, capped]);
        MigrationBadgePresentation reverse = MigrationBadgeAggregator.Aggregate([capped, unanalyzed]);

        Assert.Equal(forward.LabelTr, reverse.LabelTr);
        Assert.Equal(forward.LabelEn, reverse.LabelEn);
        Assert.True(forward.IsRestoreTierCapped);
        Assert.Contains("envanter", forward.LabelTr);
        Assert.Contains("inventory", forward.LabelEn);
    }

    [Fact]
    public void Canonical_recipe_id_groups_case_variants_and_orders_parts_by_original_ordinal()
    {
        MigrationSelectionCandidate ordinalTen = Candidate("App#10", "App", "App", "games") with
        {
            Meta = Meta("App", "App#10", PortabilityClass.ProfileRelative) with { ItemOrdinal = 10 },
        };
        MigrationSelectionCandidate ordinalTwo = Candidate("app#2", "app", "App", "games") with
        {
            Meta = Meta("app", "app#2", PortabilityClass.ProfileRelative) with { ItemOrdinal = 2 },
        };

        MigrationAppGroup app = Assert.Single(MigrationSelectionBuilder.Build([ordinalTen, ordinalTwo])
            .Single(group => group.Category == MigrationCategory.GameSaves)
            .Apps);

        Assert.Equal("app", app.RecipeId);
        Assert.Equal(["app#2", "App#10"], app.Parts.Select(part => part.Candidate.Id));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static MigrationItemMeta Meta(string recipeId, string entryId, PortabilityClass portability)
        => new(recipeId, entryId, portability, RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, Array.Empty<string>());

    private static MigrationSelectionCandidate Candidate(string id, string recipeId, string displayName, string category)
        => new()
        {
            Id = id,
            DisplayName = displayName,
            RecipeCategory = category,
            Meta = Meta(recipeId, id, PortabilityClass.ProfileRelative),
            RestoreTier = RestoreTier.ConfigCopy,
            SourceKind = MigrationSourceKind.None,
            CloudBackup = CloudBackupStatus.NotBackedUp,
            IsOnSystemDrive = true,
            IsUnique = true,
            IsRegenerable = false,
            IsRecognized = true,
            HasInstallRecord = true,
        };

    private static MigrationBadgePresentation Badge(BadgeKind kind) => kind switch
    {
        BadgeKind.PortableClean => Present(kind, "✅", mayClaimWorks: true, "taşınabilir", "portable"),
        BadgeKind.PortableWithStep => Present(kind, "🔁", mayClaimWorks: true, "adım gerekli", "step required"),
        BadgeKind.Partial => Present(kind, "⚠️", mayClaimWorks: false, "kısmi", "partial"),
        _ => Present(kind, "❌", mayClaimWorks: false, "makine-kilitli", "machine-locked"),
    };

    private static MigrationBadgePresentation Present(
        BadgeKind kind, string glyph, bool mayClaimWorks, string labelTr, string labelEn)
        => new(
            new PortabilityBadgeResult(kind, glyph, mayClaimWorks),
            kind,
            glyph,
            mayClaimWorks,
            HasSecretOverlay: false,
            HasRegenerableOverlay: false,
            IsRestoreTierCapped: false,
            LabelTr: labelTr,
            LabelEn: labelEn);
}

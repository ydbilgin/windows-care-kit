using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Selection;
using Xunit;

namespace WindowsCareKit.Tests.Migration.Selection;

public sealed class MigrationSelectionLogicTests
{
    [Fact]
    public void Badge_is_derived_from_core_and_machine_locked_never_claims_works()
    {
        MigrationBadgePresentation badge = MigrationBadgePresenter.Derive(
            Meta(PortabilityClass.MachineLocked), RestoreTier.MergeAfterInstall, isRegenerable: false);

        Assert.Equal(BadgeKind.MachineLocked, badge.CoreBadge.Kind);
        Assert.Equal("❌", badge.Glyph);
        Assert.False(badge.MayClaimWorks);
    }

    [Fact]
    public void Secret_and_regenerable_are_overlays_not_a_second_portability_source()
    {
        MigrationItemMeta meta = Meta(PortabilityClass.ProfileRelative) with { HasExcludedSecret = true };
        MigrationBadgePresentation badge = MigrationBadgePresenter.Derive(
            meta, RestoreTier.ConfigCopy, isRegenerable: true);

        Assert.Equal(BadgeKind.Partial, badge.CoreBadge.Kind);
        Assert.True(badge.HasSecretOverlay);
        Assert.True(badge.HasRegenerableOverlay);
        Assert.False(badge.MayClaimWorks);
    }

    [Fact]
    public void Inventory_only_7zip_recipe_cannot_render_green_works()
    {
        MigrationRecipe recipe = BuiltinRecipeSource.LoadAll().Single(r => r.Id == "7zip.7zip");
        var meta = new MigrationItemMeta(recipe.Id, recipe.Id + "#0", recipe.PortabilityClass,
            recipe.Restore.Strategy, recipe.Restore.Phase, recipe.Restore.Preconditions);

        MigrationBadgePresentation badge = MigrationBadgePresenter.Derive(
            meta, recipe.RestoreTier, isRegenerable: false);

        Assert.Equal(RestoreTier.InventoryOnly, recipe.RestoreTier); // loader's non-profile floor is load-bearing
        Assert.Equal(BadgeKind.PortableClean, badge.CoreBadge.Kind);
        Assert.Equal(BadgeKind.Partial, badge.DisplayKind);
        Assert.Equal("⚠️", badge.Glyph);
        Assert.False(badge.MayClaimWorks);
        Assert.True(badge.IsRestoreTierCapped);
    }

    [Fact]
    public void Smart_default_checks_only_the_top_irreplaceability_tier()
    {
        MigrationSelectionCandidate candidate = Candidate("project", "projects") with
        {
            HasCloudBackup = false,
            IsOnSystemDrive = true,
            IsUnique = true,
            IsRegenerable = false,
        };
        MigrationBadgePresentation badge = MigrationBadgePresenter.Derive(
            candidate.Meta, candidate.RestoreTier, candidate.IsRegenerable);

        SmartDefaultDecision result = SmartDefaultScorer.Score(candidate, badge);

        Assert.Equal(3, result.IrreplaceabilityScore);
        Assert.Equal(SmartDefaultKind.On, result.Kind);
    }

    [Fact]
    public void Smart_default_factor_three_uses_unique_or_non_regenerable_semantics()
    {
        MigrationSelectionCandidate candidate = Candidate("unique-but-regenerable", "projects") with
        {
            HasCloudBackup = false,
            IsOnSystemDrive = true,
            IsUnique = true,
            IsRegenerable = true,
        };
        MigrationBadgePresentation badge = MigrationBadgePresenter.Derive(
            candidate.Meta, candidate.RestoreTier, candidate.IsRegenerable);

        SmartDefaultDecision result = SmartDefaultScorer.Score(candidate, badge);

        Assert.Equal(3, result.IrreplaceabilityScore);
        Assert.Equal(SmartDefaultKind.On, result.Kind);
    }

    [Fact]
    public void Partial_and_machine_locked_buckets_are_never_prechecked()
    {
        MigrationSelectionCandidate candidate = Candidate("locked", "security") with
        {
            Meta = Meta(PortabilityClass.MachineLocked),
            HasCloudBackup = false,
            IsOnSystemDrive = true,
            IsUnique = true,
        };
        MigrationBadgePresentation badge = MigrationBadgePresenter.Derive(
            candidate.Meta, RestoreTier.MergeAfterInstall, false);

        Assert.Equal(SmartDefaultKind.Off, SmartDefaultScorer.Score(candidate, badge).Kind);
    }

    [Fact]
    public void Auto_stub_and_unrecognized_are_manual_review_only_even_with_score_three()
    {
        MigrationSelectionCandidate autoStub = Candidate("stub", "dev-tools") with
        {
            IsAutoStub = true,
            HasCloudBackup = false,
            IsOnSystemDrive = true,
            IsUnique = true,
        };
        MigrationBadgePresentation badge = MigrationBadgePresenter.Derive(
            autoStub.Meta, autoStub.RestoreTier, false);

        Assert.Equal(SmartDefaultKind.Off, SmartDefaultScorer.Score(autoStub, badge).Kind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Forced_default_requires_recognized_non_auto_stub_candidates(bool isRecognized, bool isAutoStub)
    {
        MigrationSelectionCandidate candidate = Candidate("redirected", "personal") with
        {
            OneDriveRedirectedSyncOff = true,
            IsRecognized = isRecognized,
            IsAutoStub = isAutoStub,
            HasCloudBackup = false,
            IsOnSystemDrive = true,
            IsUnique = true,
        };
        MigrationBadgePresentation badge = MigrationBadgePresenter.Derive(
            candidate.Meta, candidate.RestoreTier, candidate.IsRegenerable);

        SmartDefaultDecision decision = SmartDefaultScorer.Score(candidate, badge);

        Assert.Equal(SmartDefaultKind.Off, decision.Kind);
    }

    [Fact]
    public void Bare_candidate_defaults_to_unrecognized_without_install_record()
    {
        var candidate = new MigrationSelectionCandidate
        {
            Id = "bare",
            DisplayName = "Bare",
            RecipeCategory = "dev-tools",
            Meta = Meta(PortabilityClass.ProfileRelative),
            RestoreTier = RestoreTier.ConfigCopy,
        };

        Assert.False(candidate.IsRecognized);
        Assert.False(candidate.HasInstallRecord);
    }

    [Fact]
    public void OneDrive_redirected_sync_off_is_forced_on_and_cannot_be_cleared()
    {
        MigrationSelectionCandidate candidate = Candidate("documents", "personal") with
        {
            OneDriveRedirectedSyncOff = true,
            Meta = Meta(PortabilityClass.MachineLocked),
        };
        MigrationSelectionGroup group = MigrationSelectionBuilder.Build([candidate])
            .Single(g => g.Category == MigrationCategory.IrreplaceablePersonal);
        MigrationSelectionItem item = Assert.Single(group.Items);

        Assert.True(item.IsSelected);
        Assert.True(item.IsForcedSelected);
        item.SetSelected(false);
        Assert.True(item.IsSelected);
    }

    [Fact]
    public void Builder_always_returns_all_eight_categories_in_critical_first_order()
    {
        IReadOnlyList<MigrationSelectionGroup> groups = MigrationSelectionBuilder.Build([]);

        Assert.Equal(8, groups.Count);
        Assert.Equal(Enum.GetValues<MigrationCategory>(), groups.Select(g => g.Category));
    }

    [Fact]
    public void Unknown_and_auto_stub_rows_are_grouped_into_category_eight()
    {
        MigrationSelectionCandidate unknown = Candidate("unknown", "dev-tools") with { IsRecognized = false };
        MigrationSelectionCandidate stub = Candidate("stub", "browsers") with { IsAutoStub = true };

        MigrationSelectionGroup group = MigrationSelectionBuilder.Build([unknown, stub])
            .Single(g => g.Category == MigrationCategory.DetectedUnrecognized);

        Assert.Equal(["stub", "unknown"], group.Items.Select(i => i.Candidate.Id));
    }

    [Fact]
    public void Group_header_tracks_none_partial_all_and_item_changes()
    {
        MigrationSelectionCandidate a = Candidate("a", "lists") with { HasCloudBackup = true };
        MigrationSelectionCandidate b = Candidate("b", "lists") with { HasCloudBackup = true };
        MigrationSelectionGroup group = MigrationSelectionBuilder.Build([a, b])
            .Single(g => g.Category == MigrationCategory.ListSettingDumps);

        Assert.Equal(GroupSelectionState.None, group.SelectionState);
        group.SetItem(group.Items[0], true);
        Assert.Equal(GroupSelectionState.Partial, group.SelectionState);
        group.SetAll(true);
        Assert.Equal(GroupSelectionState.All, group.SelectionState);
        group.SetAll(false);
        Assert.Equal(GroupSelectionState.None, group.SelectionState);
    }

    [Fact]
    public void Scan_gate_stays_disabled_until_enumeration_and_profile_confirmation()
    {
        ScanReadyGate pending = ScanReadyGate.Pending(@"C:\Users\demo");
        Assert.False(pending.CanSelect);

        DetectionResult detection = Detection(programs: 2, uncovered: 1);
        ScanReadyGate complete = ScanReadyGate.Complete(detection, @"C:\Users\demo");
        Assert.False(complete.CanSelect);
        Assert.Equal(2, complete.ProgramCount);
        Assert.Equal(2, complete.SourceCount);

        ScanReadyGate confirmed = complete.ConfirmProfile();
        Assert.True(confirmed.CanSelect);
        Assert.Contains(@"C:\Users\demo", confirmed.ConfirmationEn);
    }

    [Fact]
    public void Wrong_profile_warning_and_other_user_scope_are_both_non_vacuous()
    {
        var otherUser = new DiscoveredProgram
        {
            Id = "other-user-app",
            DisplayName = "Other user app",
            NormalizedName = "other user app",
            Scope = ProgramScope.OtherUserNotEnumerable,
            Sources = [ProgramSourceKind.Msi],
        };
        var detection = new DetectionResult(
            [otherUser],
            [new ProgramSourceReport(ProgramSourceKind.Msi, ProgramSourceStatus.Ok, 1)]);

        ScanReadyGate gate = ScanReadyGate.Complete(detection, @"C:\Users\admin");

        Assert.False(gate.CanSelect);
        Assert.Equal("admin", gate.ProfileName);
        Assert.Contains("different user", gate.ConfirmationEn, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(detection.Programs, p => p.Scope == ProgramScope.OtherUserNotEnumerable);
    }

    [Fact]
    public void Coverage_keeps_app_reinstall_config_restore_and_detection_as_three_integer_ratios()
    {
        MigrationSelectionCandidate automatic = Candidate("automatic", "games") with
        {
            InstallMethod = RecipeInstallMethod.Winget,
            RestoreTier = RestoreTier.ConfigCopy,
            HasInstallRecord = true,
        };
        MigrationSelectionCandidate npm = Candidate("npm", "games") with
        {
            InstallMethod = RecipeInstallMethod.Npm,
            RestoreTier = RestoreTier.InventoryOnly,
            HasInstallRecord = false,
        };
        MigrationSelectionCandidate manualDownload = Candidate("manual-download", "games") with
        {
            InstallMethod = RecipeInstallMethod.UrlManual,
            RestoreTier = RestoreTier.ConfigCopy,
            HasInstallRecord = true,
        };
        MigrationSelectionCandidate configOnly = Candidate("config-only", "games") with
        {
            InstallMethod = null,
            RestoreTier = RestoreTier.ConfigCopy,
            HasInstallRecord = true,
        };
        MigrationSelectionCandidate inventoryOnly = Candidate("inventory-only", "games") with
        {
            InstallMethod = null,
            RestoreTier = RestoreTier.InventoryOnly,
            HasInstallRecord = true,
        };
        MigrationSelectionCandidate uncovered = Candidate("uncovered", "games") with
        {
            InstallMethod = null,
            RestoreTier = RestoreTier.InventoryOnly,
            HasInstallRecord = false,
        };
        IReadOnlyList<MigrationSelectionGroup> groups = MigrationSelectionBuilder.Build(
            [automatic, npm, manualDownload, configOnly, inventoryOnly, uncovered]);

        CategoryCoverage games = MigrationCoverageCalculator.ByCategory(groups)
            .Single(c => c.Category == MigrationCategory.GameSaves);

        Assert.Equal(new CoverageRatio(2, 6), games.AppReinstallAvailable);
        Assert.Equal(new CoverageRatio(3, 6), games.ConfigRestoreAvailable);
        Assert.Equal(new CoverageRatio(4, 6), games.DetectionCoverage);
        Assert.Equal("2/6", games.AppReinstallAvailable.ToString());
    }

    [Fact]
    public void Detection_recall_oracle_keeps_unbacked_launchables_in_denominator()
    {
        CoverageRatio coverage = MigrationCoverageCalculator.DetectionCoverage(
            Detection(programs: 5, uncovered: 2));

        Assert.Equal(new CoverageRatio(3, 5), coverage);
    }

    [Fact]
    public void Banner_states_restore_detection_and_user_owned_transport_without_fake_precision()
    {
        FeasibilityCeilingText banner = MigrationCoverageCalculator.BuildBanner(
            Detection(programs: 5, uncovered: 2));

        Assert.Contains("3/5", banner.Tr);
        Assert.Contains("Backed up", banner.En);
        Assert.Contains("SENİN", banner.TransportTr);
        Assert.Contains("No LAN/live transfer", banner.TransportEn);
        Assert.DoesNotContain("%", banner.Tr);
    }

    [Fact]
    public void Directory_preview_is_exact_robocopy_string_and_never_executes()
    {
        MigrationSelectionCandidate candidate = Candidate("dir", "personal") with
        {
            SourceKind = MigrationSourceKind.Directory,
            SourcePath = @"C:\Users\demo\Projects",
            DestinationPath = @"E:\WCK\Projects",
        };

        string command = MigrationCommandPreviewGenerator.Generate(candidate)!;

        Assert.Equal(
            "robocopy 'C:\\Users\\demo\\Projects' 'E:\\WCK\\Projects' /E /COPY:DAT /R:1 /W:1 /XJ",
            command);
    }

    [Fact]
    public void File_preview_uses_literal_path_and_quotes_apostrophes_against_injection()
    {
        MigrationSelectionCandidate candidate = Candidate("file", "dev-tools") with
        {
            SourceKind = MigrationSourceKind.File,
            SourcePath = @"C:\Users\demo\O'Brien;$(Get-Process).json",
            DestinationPath = @"E:\WCK\settings.json",
        };

        string command = MigrationCommandPreviewGenerator.Generate(candidate)!;

        Assert.Equal(
            "Copy-Item -LiteralPath 'C:\\Users\\demo\\O''Brien;$(Get-Process).json' -Destination 'E:\\WCK\\settings.json' -Force",
            command);
        Assert.DoesNotContain("\r", command);
        Assert.DoesNotContain("\n", command);
    }

    [Fact]
    public void Preview_rejects_control_character_path_instead_of_emitting_a_second_command()
    {
        MigrationSelectionCandidate candidate = Candidate("bad", "dev-tools") with
        {
            SourceKind = MigrationSourceKind.File,
            SourcePath = "C:\\safe\r\nRemove-Item C:\\",
            DestinationPath = @"E:\WCK\safe",
        };

        Assert.Throws<ArgumentException>(() => MigrationCommandPreviewGenerator.Generate(candidate));
    }

    [Fact]
    public void Combined_honesty_and_recipe_manual_todo_are_rendered_for_success_checklist()
    {
        MigrationSelectionCandidate candidate = Candidate("chrome", "browsers") with
        {
            Meta = Meta(PortabilityClass.MachineLocked),
            BackedUpButNotRestored = true,
            RequiresRelogin = true,
            ManualTodo = ["Export passwords to CSV before formatting."],
        };
        MigrationSelectionItem item = MigrationSelectionBuilder.Build([candidate])
            .Single(g => g.Category == MigrationCategory.Browsers).Items.Single();

        IReadOnlyList<ManualTodoEntry> todos = ManualTodoRenderer.Render(item);

        Assert.Contains(todos, t => t.Code == "recipe-manual-todo");
        Assert.Contains(todos, t => t.Code == "combined-honesty" && t.IsCritical);
        Assert.Contains(todos, t => t.Code == "relogin-required");
    }

    private static MigrationItemMeta Meta(PortabilityClass portability)
        => new("recipe", "entry", portability, RestoreStrategy.ConfigWrite,
            RestorePhase.ConfigWrite, Array.Empty<string>());

    private static MigrationSelectionCandidate Candidate(string id, string category)
        => new()
        {
            Id = id,
            DisplayName = id,
            RecipeCategory = category,
            Meta = Meta(PortabilityClass.ProfileRelative),
            RestoreTier = RestoreTier.ConfigCopy,
            SourceKind = MigrationSourceKind.None,
            HasCloudBackup = true,
            IsOnSystemDrive = false,
            IsUnique = false,
            IsRegenerable = true,
            IsRecognized = true,
            HasInstallRecord = true,
        };

    private static DetectionResult Detection(int programs, int uncovered)
    {
        var list = Enumerable.Range(0, programs)
            .Select(i => new DiscoveredProgram
            {
                Id = $"app-{i}",
                DisplayName = $"App {i}",
                NormalizedName = $"app {i}",
                Scope = ProgramScope.CurrentUser,
                Sources = [ProgramSourceKind.RegistryUninstall],
            }).ToArray();
        return new DetectionResult(
            list,
            [
                new ProgramSourceReport(ProgramSourceKind.RegistryUninstall, ProgramSourceStatus.Ok, programs),
                new ProgramSourceReport(ProgramSourceKind.StartMenu, ProgramSourceStatus.Ok, uncovered),
            ],
            uncovered);
    }
}

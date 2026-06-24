using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Selection;
using Xunit;

namespace WindowsCareKit.Tests;

public sealed class MigrationViewModelTests
{
    [Fact]
    public void LoadScan_runs_detection_badge_grouping_and_gate_flow_without_a_view()
    {
        var vm = new MigrationViewModel();
        MigrationSelectionCandidate project = Candidate("project", "projects");
        MigrationSelectionCandidate locked = Candidate("browser-secret", "browsers") with
        {
            Meta = Meta(PortabilityClass.MachineLocked),
            BackedUpButNotRestored = true,
        };

        vm.LoadScan(Detection(2, 1), @"C:\Users\demo", [project, locked]);

        Assert.True(vm.IsScanComplete);
        Assert.False(vm.CanSelect);
        Assert.Equal(8, vm.Groups.Count);
        Assert.Equal(2, vm.ScanGate!.ProgramCount);
        Assert.Equal(new CoverageRatio(1, 2), vm.Ceiling!.DetectionCoverage);
        MigrationItemRow lockedRow = vm.Groups
            .Single(group => group.Category == MigrationCategory.Browsers).Items.Single();
        Assert.Equal("❌", lockedRow.Badge.Glyph);
        Assert.False(lockedRow.Badge.MayClaimWorks);

        Assert.False(vm.PreviewCommandsCommand.CanExecute(null));
        vm.ConfirmProfileCommand.Execute(null);
        Assert.True(vm.CanSelect);
        Assert.True(vm.PreviewCommandsCommand.CanExecute(null)); // project smart-default is selected
    }

    [Fact]
    public void Group_and_item_commands_preserve_three_state_and_forced_selection()
    {
        var vm = new MigrationViewModel();
        MigrationSelectionCandidate optional = Candidate("optional", "personal") with
        {
            HasCloudBackup = true,
            IsOnSystemDrive = false,
            IsUnique = false,
            IsRegenerable = true,
        };
        MigrationSelectionCandidate forced = Candidate("forced", "personal") with
        {
            OneDriveRedirectedSyncOff = true,
            Meta = Meta(PortabilityClass.MachineLocked),
        };
        vm.LoadScan(Detection(2, 0), @"C:\Users\demo", [optional, forced]);
        vm.ConfirmProfileCommand.Execute(null);
        MigrationGroupRow group = vm.Groups.Single(g => g.Category == MigrationCategory.IrreplaceablePersonal);

        Assert.Null(group.IsChecked); // forced selected, optional off
        vm.ToggleGroupCommand.Execute(group);
        Assert.True(group.IsChecked);

        MigrationItemRow optionalRow = group.Items.Single(i => i.Candidate.Id == "optional");
        vm.ToggleItemCommand.Execute(optionalRow);
        Assert.Null(group.IsChecked);

        vm.ClearOptionalCommand.Execute(null);
        Assert.Null(group.IsChecked);
        Assert.True(group.Items.Single(i => i.Candidate.Id == "forced").IsSelected);
        Assert.False(optionalRow.IsSelected);
    }

    [Fact]
    public void Preview_command_is_string_only_and_manual_todo_keeps_combined_honesty()
    {
        var vm = new MigrationViewModel();
        MigrationSelectionCandidate candidate = Candidate("browser", "browsers") with
        {
            Meta = Meta(PortabilityClass.MachineLocked),
            OneDriveRedirectedSyncOff = true,
            SourceKind = MigrationSourceKind.File,
            SourcePath = @"C:\Users\demo\Bookmarks",
            DestinationPath = @"E:\WCK\Bookmarks",
            BackedUpButNotRestored = true,
            RequiresRelogin = true,
            ManualTodo = ["Export passwords before formatting."],
        };
        vm.LoadScan(Detection(1, 0), @"C:\Users\demo", [candidate]);
        vm.ConfirmProfileCommand.Execute(null);

        vm.PreviewCommandsCommand.Execute(null);

        Assert.Equal(
            "Copy-Item -LiteralPath 'C:\\Users\\demo\\Bookmarks' -Destination 'E:\\WCK\\Bookmarks' -Force",
            Assert.Single(vm.CommandPreview));
        Assert.Contains(vm.ManualTodo, todo => todo.Code == "combined-honesty");
        Assert.Contains(vm.ManualTodo, todo => todo.Code == "recipe-manual-todo");
        Assert.Contains(vm.ManualTodo, todo => todo.Code == "relogin-required");
    }

    [Fact]
    public void Selection_change_invalidates_stale_preview()
    {
        var vm = new MigrationViewModel();
        vm.LoadScan(Detection(1, 0), @"C:\Users\demo", [Candidate("project", "projects")]);
        vm.ConfirmProfileCommand.Execute(null);
        vm.PreviewCommandsCommand.Execute(null);
        Assert.True(vm.HasCommandPreview);

        MigrationItemRow row = vm.Groups
            .Single(group => group.Category == MigrationCategory.IrreplaceablePersonal).Items.Single();
        vm.ToggleItemCommand.Execute(row);

        Assert.False(vm.HasCommandPreview);
        Assert.Empty(vm.CommandPreview);
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
            SourceKind = MigrationSourceKind.Directory,
            SourcePath = $@"C:\Users\demo\{id}",
            DestinationPath = $@"E:\WCK\{id}",
            HasCloudBackup = false,
            IsOnSystemDrive = true,
            IsUnique = true,
            IsRegenerable = false,
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
            [new ProgramSourceReport(ProgramSourceKind.RegistryUninstall, ProgramSourceStatus.Ok, programs)],
            uncovered);
    }
}

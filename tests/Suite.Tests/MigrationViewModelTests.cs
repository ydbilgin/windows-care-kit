using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Execution;
using WindowsCareKit.Core.Modules.Migration.Selection;
using Xunit;

namespace WindowsCareKit.Tests;

public sealed class MigrationViewModelTests
{
    [Fact]
    public void Constructor_requires_injected_seams_and_does_not_scan()
    {
        var scan = new FakeScanService(
            new MigrationScanResult(Detection(0, 0), @"C:\Users\demo", []));

        var vm = new MigrationViewModel(new I18n(), scan);

        Assert.NotNull(vm);
        Assert.Equal(0, scan.CallCount);
        Assert.DoesNotContain(
            typeof(MigrationViewModel).GetConstructors(),
            constructor => constructor.GetParameters().Length == 0);
    }

    [Fact]
    public void LoadScan_runs_detection_badge_grouping_and_gate_flow_without_a_view()
    {
        MigrationViewModel vm = CreateVm();
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
        MigrationViewModel vm = CreateVm();
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
        MigrationViewModel vm = CreateVm();
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
        MigrationViewModel vm = CreateVm();
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

    [Fact]
    public async Task StartScanAsync_uses_fake_once_populates_state_and_restore_stays_disabled()
    {
        MigrationSelectionCandidate candidate = Candidate("project", "projects");
        var scan = new FakeScanService(new MigrationScanResult(
            Detection(1, 0), @"C:\Users\demo", [candidate]));
        MigrationViewModel vm = CreateVm(scan);

        Assert.False(vm.IsScanComplete);
        Assert.False(vm.CanSelect);
        Assert.Empty(vm.Groups);
        Assert.False(vm.CanRestoreExecute);

        await vm.StartScanAsync();
        await vm.StartScanAsync();

        Assert.Equal(1, scan.CallCount);
        Assert.True(vm.IsScanComplete);
        Assert.True(vm.CanSelect);
        Assert.Equal(8, vm.Groups.Count);
        Assert.Equal(new CoverageRatio(1, 1), vm.Ceiling!.DetectionCoverage);
        Assert.Equal("✅", vm.Groups
            .Single(group => group.Category == MigrationCategory.IrreplaceablePersonal)
            .Items.Single().Badge.Glyph);
        Assert.False(vm.CanRestoreExecute);
    }

    [Fact]
    public async Task StartScanAsync_is_reentrancy_safe_while_fake_scan_is_blocked()
    {
        using var release = new ManualResetEventSlim();
        var scan = new BlockingScanService(
            new MigrationScanResult(Detection(0, 0), @"C:\Users\demo", []),
            release);
        MigrationViewModel vm = CreateVm(scan);

        Task first = vm.StartScanAsync();
        Assert.True(SpinWait.SpinUntil(() => scan.CallCount == 1, TimeSpan.FromSeconds(2)));
        Task second = vm.StartScanAsync();

        Assert.True(second.IsCompleted);
        Assert.Equal(1, scan.CallCount);
        release.Set();
        await first;
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task Cancelled_scan_can_be_retried_after_cleanup()
    {
        var scan = new CancelThenSucceedScanService(
            new MigrationScanResult(Detection(0, 0), @"C:\Users\demo", []));
        MigrationViewModel vm = CreateVm(scan);

        Task first = vm.StartScanAsync();
        Assert.True(SpinWait.SpinUntil(() => scan.CallCount == 1, TimeSpan.FromSeconds(2)));
        vm.CancelScan();
        await first;
        await vm.StartScanAsync();

        Assert.Equal(2, scan.CallCount);
        Assert.True(vm.IsScanComplete);
        Assert.False(vm.IsScanning);
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

    private static MigrationViewModel CreateVm(IMigrationScanService? scan = null)
        => new(new I18n(), scan ?? new FakeScanService(
            new MigrationScanResult(Detection(0, 0), @"C:\Users\demo", [])));

    private sealed class FakeScanService(MigrationScanResult result) : IMigrationScanService
    {
        public int CallCount { get; private set; }

        public MigrationScanResult Scan(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return result;
        }
    }

    private sealed class BlockingScanService(
        MigrationScanResult result,
        ManualResetEventSlim release) : IMigrationScanService
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public MigrationScanResult Scan(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            release.Wait(cancellationToken);
            return result;
        }
    }

    private sealed class CancelThenSucceedScanService(MigrationScanResult result) : IMigrationScanService
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public MigrationScanResult Scan(CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                using var wait = new ManualResetEventSlim();
                wait.Wait(cancellationToken);
            }
            return result;
        }
    }
}

using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Execution;
using WindowsCareKit.Core.Modules.Migration.Selection;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

public sealed class MigrationViewModelTests
{
    [Fact]
    public void Constructor_requires_injected_seams_and_does_not_scan()
    {
        var scan = new FakeScanService(
            new MigrationScanResult(Detection(0, 0), @"C:\Users\demo", []));
        var runner = new RecordingMigrationBackupRunner();

        var vm = new MigrationViewModel(new I18n(), scan, runner, () => Array.Empty<MigrationRecipe>());

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
    public void Locked_now_candidate_renders_close_app_reason()
    {
        I18n i18n = TestI18n.Full("en");
        MigrationViewModel vm = CreateVm(i18n: i18n);
        MigrationSelectionCandidate locked = Candidate("firefox-profile", "browsers") with
        {
            Meta = new MigrationItemMeta(
                "recipe",
                "entry",
                PortabilityClass.ProfileRelative,
                RestoreStrategy.ConfigWrite,
                RestorePhase.ConfigWrite,
                ["process-closed:firefox.exe"])
            {
                HasUnanalyzedContent = true,
                ContentProbeStatus = ContentProbeStatus.LockedNow,
            },
        };

        vm.LoadScan(Detection(1, 0), @"C:\Users\demo", [locked]);

        MigrationItemRow row = vm.Groups.Single(group => group.Category == MigrationCategory.Browsers).Items.Single();
        Assert.Equal("in use - close firefox.exe and re-scan", row.WhatHappens);
    }

    [Fact]
    public void Locked_now_language_keys_exist_in_english_and_turkish()
    {
        HashSet<string> en = ReadLangKeys("en");
        HashSet<string> tr = ReadLangKeys("tr");

        Assert.Contains("migration.item.reason.lockedNow", en);
        Assert.Contains("migration.item.reason.lockedNow.generic", en);
        Assert.Contains("migration.item.reason.lockedNow", tr);
        Assert.Contains("migration.item.reason.lockedNow.generic", tr);
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
    public void Forced_item_rejects_uncheck_and_notifies_the_checkbox_to_snap_back()
    {
        I18n i18n = TestI18n.Full("en");
        MigrationViewModel vm = CreateVm(i18n: i18n);
        MigrationSelectionCandidate forced = Candidate("forced", "personal") with
        {
            OneDriveRedirectedSyncOff = true,
            Meta = Meta(PortabilityClass.MachineLocked),
        };
        vm.LoadScan(Detection(1, 0), @"C:\Users\demo", [forced]);
        vm.ConfirmProfileCommand.Execute(null);
        MigrationItemRow row = vm.Groups
            .Single(g => g.Category == MigrationCategory.IrreplaceablePersonal)
            .Items.Single();
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.IsSelected = false;

        Assert.True(row.IsForcedSelected);
        Assert.True(row.IsSelected);
        Assert.Contains(nameof(MigrationItemRow.IsSelected), changed);
        Assert.Equal("required — always carried", row.ForcedSelectionToolTip);
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
    public async Task StartScanAsync_uses_fake_once_and_populates_state()
    {
        MigrationSelectionCandidate candidate = Candidate("project", "projects");
        var scan = new FakeScanService(new MigrationScanResult(
            Detection(1, 0), @"C:\Users\demo", [candidate]));
        MigrationViewModel vm = CreateVm(scan);

        Assert.False(vm.IsScanComplete);
        Assert.False(vm.CanSelect);
        Assert.Empty(vm.Groups);

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
    }

    [Fact]
    public async Task BuildCapturePlan_uses_exactly_the_distinct_selected_recipe_ids()
    {
        var runner = new RecordingMigrationBackupRunner();
        MigrationRecipe recipeA = Recipe("recipe-a", "a.cfg");
        MigrationRecipe recipeB = Recipe("recipe-b", "b.cfg");
        MigrationRecipe recipeC = Recipe("recipe-c", "c.cfg");
        MigrationViewModel vm = CreateVm(runner: runner, recipes: [recipeA, recipeB, recipeC]);
        vm.LoadScan(Detection(2, 0), @"C:\Users\demo",
        [
            Candidate("a", "projects", "recipe-a"),
            Candidate("b", "dev-tools", "recipe-b"),
        ]);
        vm.ConfirmProfileCommand.Execute(null);
        vm.PackageDir = OutsideAppPackage();

        await vm.BuildCapturePlanAsync();

        Assert.Equal(["recipe-a", "recipe-b"], runner.LastRecipeIds);
        Assert.Equal(2, vm.CapturePlanRows.Count);
        Assert.True(vm.HasCapturePlan);
        Assert.False(vm.CanRunCapture);
    }

    [Fact]
    public async Task Partial_recipe_selection_shows_the_full_per_file_runner_plan_before_approval()
    {
        var runner = new RecordingMigrationBackupRunner();
        MigrationRecipe recipe = Recipe("recipe-a", "selected.cfg", "other.cfg");
        MigrationSelectionCandidate selected = Candidate("selected", "projects", "recipe-a");
        MigrationSelectionCandidate optional = Candidate("optional", "projects", "recipe-a") with
        {
            HasCloudBackup = true,
            IsOnSystemDrive = false,
            IsUnique = false,
            IsRegenerable = true,
        };
        MigrationViewModel vm = CreateVm(runner: runner, recipes: [recipe]);
        vm.LoadScan(Detection(2, 0), @"C:\Users\demo", [selected, optional]);
        vm.ConfirmProfileCommand.Execute(null);
        Assert.Equal(1, vm.SelectedCount);
        vm.PackageDir = OutsideAppPackage();

        await vm.BuildCapturePlanAsync();

        Assert.Equal(["recipe-a"], runner.LastRecipeIds);
        Assert.Equal(2, vm.CapturePlanRows.Count);
        Assert.Contains(vm.CapturePlanRows, row => row.Text.Contains("selected.cfg", StringComparison.Ordinal));
        Assert.Contains(vm.CapturePlanRows, row => row.Text.Contains("other.cfg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Capture_run_requires_approval_and_passes_the_previewed_plan_hash()
    {
        var runner = new RecordingMigrationBackupRunner();
        MigrationViewModel vm = CreateCaptureVm(runner);

        await vm.BuildCapturePlanAsync();
        await vm.RunCaptureAsync();
        Assert.Equal(0, runner.RunCount);
        Assert.False(vm.CanRunCapture);

        vm.IsPreviewApproved = true;
        Assert.True(vm.CanRunCapture);
        await vm.RunCaptureAsync();

        Assert.Equal(1, runner.RunCount);
        Assert.Equal(runner.LastPlan!.Plan.ComputeHash(), runner.LastApprovedHash);
        Assert.True(vm.HasCaptureResults);
    }

    [Fact]
    public async Task Runner_hash_refusal_is_surfaced_and_reports_no_copied_success()
    {
        var runner = new RecordingMigrationBackupRunner { RefuseAsHashMismatch = true };
        MigrationViewModel vm = CreateCaptureVm(runner);
        await vm.BuildCapturePlanAsync();
        vm.IsPreviewApproved = true;

        await vm.RunCaptureAsync();

        Assert.Equal(1, runner.RunCount);
        Assert.StartsWith("migration.capture.refused", vm.CaptureSummary, StringComparison.Ordinal);
        Assert.Single(vm.CaptureResultRows);
        Assert.Equal("SKIPPED", vm.CaptureResultRows[0].RiskText);
    }

    [Fact]
    public async Task BuildCapturePlan_surfaces_honest_runner_skips()
    {
        var runner = new RecordingMigrationBackupRunner
        {
            PlanSkips = [new RecipeItemSkip("secret.db", "forbidden secret store")],
        };
        MigrationViewModel vm = CreateCaptureVm(runner);

        await vm.BuildCapturePlanAsync();

        PlanRow skip = Assert.Single(vm.CaptureSkippedRows);
        Assert.Equal("secret.db", skip.Text);
        Assert.Contains("forbidden secret", skip.Detail);
    }

    [Fact]
    public async Task PackageDir_inside_app_is_rejected_before_runner_plan_build()
    {
        var runner = new RecordingMigrationBackupRunner();
        MigrationViewModel vm = CreateCaptureVm(runner);
        vm.PackageDir = Path.Combine(AppContext.BaseDirectory, "capture-package");

        await vm.BuildCapturePlanAsync();

        Assert.Equal(0, runner.BuildCount);
        Assert.False(vm.HasCapturePlan);
        Assert.Equal("migration.capture.outsideAppWarning", vm.PackageWarning);
    }

    [Fact]
    public async Task Selection_change_invalidates_capture_plan_and_approval()
    {
        var runner = new RecordingMigrationBackupRunner();
        MigrationViewModel vm = CreateCaptureVm(runner);
        await vm.BuildCapturePlanAsync();
        vm.IsPreviewApproved = true;
        Assert.True(vm.CanRunCapture);

        MigrationItemRow row = vm.Groups
            .Single(group => group.Category == MigrationCategory.IrreplaceablePersonal).Items.Single();
        vm.ToggleItemCommand.Execute(row);

        Assert.False(vm.HasCapturePlan);
        Assert.False(vm.IsPreviewApproved);
        Assert.False(vm.CanRunCapture);
    }

    [Fact]
    public async Task Destination_change_invalidates_capture_plan_and_approval()
    {
        var runner = new RecordingMigrationBackupRunner();
        MigrationViewModel vm = CreateCaptureVm(runner);
        await vm.BuildCapturePlanAsync();
        vm.IsPreviewApproved = true;
        Assert.True(vm.CanRunCapture);

        // Re-pointing the backup destination must discard the plan approved for the OLD destination, so an
        // approved-then-redirected capture can never run against a folder the user never saw a plan for.
        vm.PackageDir = OutsideAppPackage();

        Assert.False(vm.HasCapturePlan);
        Assert.False(vm.IsPreviewApproved);
        Assert.False(vm.CanRunCapture);
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

    private static MigrationSelectionCandidate Candidate(string id, string category, string recipeId = "recipe")
        => new()
        {
            Id = id,
            DisplayName = id,
            RecipeCategory = category,
            Meta = new MigrationItemMeta(
                recipeId,
                id,
                PortabilityClass.ProfileRelative,
                RestoreStrategy.ConfigWrite,
                RestorePhase.ConfigWrite,
                Array.Empty<string>()),
            RestoreTier = RestoreTier.ConfigCopy,
            SourceKind = MigrationSourceKind.Directory,
            SourcePath = $@"C:\Users\demo\{id}",
            DestinationPath = $@"E:\WCK\{id}",
            HasCloudBackup = false,
            IsOnSystemDrive = true,
            IsUnique = true,
            IsRegenerable = false,
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
            [new ProgramSourceReport(ProgramSourceKind.RegistryUninstall, ProgramSourceStatus.Ok, programs)],
            uncovered);
    }

    private static MigrationViewModel CreateVm(
        IMigrationScanService? scan = null,
        RecordingMigrationBackupRunner? runner = null,
        IReadOnlyList<MigrationRecipe>? recipes = null,
        I18n? i18n = null)
        => new(
            i18n ?? new I18n(),
            scan ?? new FakeScanService(new MigrationScanResult(Detection(0, 0), @"C:\Users\demo", [])),
            runner ?? new RecordingMigrationBackupRunner(),
            () => recipes ?? Array.Empty<MigrationRecipe>());

    private static HashSet<string> ReadLangKeys(string code)
    {
        string path = Path.Combine(FindRepositoryRoot(), "src", "Suite.Module.Migration", "lang", code + ".json");
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WindowsCareKit.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("repository root not found");
    }

    private static MigrationViewModel CreateCaptureVm(RecordingMigrationBackupRunner runner)
    {
        MigrationRecipe recipe = Recipe("recipe-a", "settings.json");
        MigrationViewModel vm = CreateVm(runner: runner, recipes: [recipe]);
        vm.LoadScan(Detection(1, 0), @"C:\Users\demo", [Candidate("settings", "projects", "recipe-a")]);
        vm.ConfirmProfileCommand.Execute(null);
        vm.PackageDir = OutsideAppPackage();
        return vm;
    }

    private static string OutsideAppPackage()
        => Path.Combine(Path.GetTempPath(), "wck-migration-vm-" + Guid.NewGuid().ToString("N"));

    private static MigrationRecipe Recipe(string id, params string[] itemPaths)
        => new(
            1,
            id,
            id,
            "projects",
            new RecipeDetect(KnownFolder.UserProfile, itemPaths[0], true),
            itemPaths.Select(path => new RecipeItem(path, Array.Empty<string>(), Array.Empty<string>())).ToArray(),
            Array.Empty<string>(),
            "global",
            PortabilityClass.ProfileRelative,
            new RecipeRestore(RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, Array.Empty<string>()));

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

    private sealed class RecordingMigrationBackupRunner : IMigrationBackupRunner
    {
        public int BuildCount { get; private set; }
        public int RunCount { get; private set; }
        public string[] LastRecipeIds { get; private set; } = [];
        public MigrationBackupPlanResult? LastPlan { get; private set; }
        public string? LastApprovedHash { get; private set; }
        public IReadOnlyList<RecipeItemSkip> PlanSkips { get; init; } = Array.Empty<RecipeItemSkip>();
        public bool RefuseAsHashMismatch { get; init; }

        public MigrationBackupPlanResult BuildPlan(
            IEnumerable<MigrationRecipe> recipes,
            string packageDir,
            DateTime utc)
        {
            BuildCount++;
            MigrationRecipe[] selected = recipes.ToArray();
            LastRecipeIds = selected.Select(recipe => recipe.Id).ToArray();
            PlannedAction[] actions = selected
                .SelectMany(recipe => recipe.Items.Select(item => (PlannedAction)new CopyAction
                {
                    Source = Path.Combine(@"C:\Users\demo", item.Path),
                    Destination = Path.Combine(packageDir, recipe.Id, item.Path),
                    Description = recipe.DisplayName,
                    Reason = "migration backup",
                    Risk = RiskLevel.Low,
                    Undo = UndoCapability.None,
                }))
                .ToArray();
            LastPlan = new MigrationBackupPlanResult(
                new OperationPlan("Migration backup", "migration-backup", actions, utc),
                PlanSkips);
            return LastPlan;
        }

        public MigrationBackupRunResult Run(
            MigrationBackupPlanResult plan,
            string approvedPlanHash,
            string packageDir)
        {
            RunCount++;
            LastPlan = plan;
            LastApprovedHash = approvedPlanHash;
            bool authorized = !RefuseAsHashMismatch
                              && string.Equals(plan.Plan.ComputeHash(), approvedPlanHash, StringComparison.Ordinal);
            CopyFileOutcome[] outcomes = plan.Plan.Actions.OfType<CopyAction>()
                .Select(action => new CopyFileOutcome(
                    action.Id,
                    action.Source,
                    action.Destination,
                    authorized,
                    authorized ? null : CopySkipReason.Blocked,
                    authorized ? "done" : "approved plan hash mismatch"))
                .ToArray();
            return new MigrationBackupRunResult(
                authorized,
                new CopySkipReport(outcomes),
                new MigrationRestoreManifest(
                    MigrationRestoreManifest.CurrentSchemaVersion,
                    Array.Empty<MigrationRestoreTarget>()),
                plan.SkippedItems,
                Array.Empty<RecipeItemSkip>());
        }
    }
}

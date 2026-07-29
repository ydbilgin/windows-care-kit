using System.Collections.Concurrent;
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

        var vm = new MigrationViewModel(
            new I18n(),
            scan,
            runner,
            () => Array.Empty<MigrationRecipe>(),
            TestData.PayloadRoots());

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
        MigrationSelectionCandidate optional = Candidate("optional", "personal", "optional") with
        {
            CloudBackup = CloudBackupStatus.BackedUp,
            IsOnSystemDrive = false,
            IsUnique = false,
            IsRegenerable = true,
        };
        MigrationSelectionCandidate forced = Candidate("forced", "personal", "forced") with
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
    public async Task App_level_selection_normalizes_defaults_keeps_forced_apps_uniform_and_drives_capture_from_apps()
    {
        var runner = new RecordingMigrationBackupRunner();
        MigrationRecipe normalRecipe = Recipe("normal", "recommended.json", "optional.json");
        MigrationRecipe forcedRecipe = Recipe("forced", "critical.json", "other.json");
        MigrationSelectionCandidate normalRecommended = Candidate("normal#0", "personal", "normal") with
        {
            Meta = new MigrationItemMeta("normal", "normal#0", PortabilityClass.ProfileRelative,
                RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, []) { ItemOrdinal = 0 },
        };
        MigrationSelectionCandidate normalOptional = Candidate("normal#1", "personal", "normal") with
        {
            Meta = new MigrationItemMeta("normal", "normal#1", PortabilityClass.ProfileRelative,
                RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, []) { ItemOrdinal = 1 },
            CloudBackup = CloudBackupStatus.BackedUp, IsOnSystemDrive = false, IsUnique = false, IsRegenerable = true,
        };
        MigrationSelectionCandidate forcedCritical = Candidate("forced#0", "personal", "forced") with
        {
            Meta = new MigrationItemMeta("forced", "forced#0", PortabilityClass.MachineLocked,
                RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, []) { ItemOrdinal = 0 },
            OneDriveRedirectedSyncOff = true,
        };
        MigrationSelectionCandidate forcedOther = Candidate("forced#1", "personal", "forced") with
        {
            Meta = new MigrationItemMeta("forced", "forced#1", PortabilityClass.MachineLocked,
                RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, []) { ItemOrdinal = 1 },
            CloudBackup = CloudBackupStatus.BackedUp, IsOnSystemDrive = false, IsUnique = false, IsRegenerable = true,
        };
        MigrationViewModel vm = CreateVm(runner: runner, recipes: [normalRecipe, forcedRecipe]);
        vm.LoadScan(Detection(4, 0), @"C:\Users\demo", [normalRecommended, normalOptional, forcedCritical, forcedOther]);
        vm.ConfirmProfileCommand.Execute(null);
        MigrationGroupRow group = vm.Groups.Single(g => g.Category == MigrationCategory.IrreplaceablePersonal);

        Assert.True(group.IsChecked);
        Assert.All(group.Apps.SelectMany(app => app.Parts), part => Assert.True(part.IsSelected));

        vm.ClearOptionalCommand.Execute(null);

        Assert.Null(group.IsChecked); // one visible app cleared, the forced app stays selected
        Assert.All(group.Apps.Single(app => app.RecipeId == "normal").Parts, part => Assert.False(part.IsSelected));
        Assert.All(group.Apps.Single(app => app.RecipeId == "forced").Parts, part => Assert.True(part.IsSelected));
        vm.PackageDir = OutsideAppPackage();

        await vm.BuildCapturePlanAsync();

        Assert.Equal(["forced"], runner.LastRecipeIds);
    }

    [Fact]
    public async Task Canonical_app_identity_is_shared_by_grouping_and_capture_lookup()
    {
        var runner = new RecordingMigrationBackupRunner();
        MigrationRecipe recipe = Recipe("App", "one.json", "two.json");
        MigrationSelectionCandidate upper = Candidate("App#0", "games", "App") with
        {
            Meta = new MigrationItemMeta("App", "App#0", PortabilityClass.ProfileRelative,
                RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, []) { ItemOrdinal = 0 },
        };
        MigrationSelectionCandidate lower = Candidate("app#1", "games", "app") with
        {
            Meta = new MigrationItemMeta("app", "app#1", PortabilityClass.ProfileRelative,
                RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, []) { ItemOrdinal = 1 },
        };
        MigrationViewModel vm = CreateVm(runner: runner, recipes: [recipe]);
        vm.LoadScan(Detection(2, 0), @"C:\Users\demo", [upper, lower]);
        vm.ConfirmProfileCommand.Execute(null);
        vm.PackageDir = OutsideAppPackage();

        await vm.BuildCapturePlanAsync();

        MigrationAppRow app = Assert.Single(vm.Groups.Single(g => g.Category == MigrationCategory.GameSaves).Apps);
        Assert.Equal("app", app.RecipeId);
        Assert.Equal(["App"], runner.LastRecipeIds);
    }

    [Fact]
    public void App_subtitle_uses_recipe_warning_and_is_stable_when_locked_and_unlocked_parts_are_reordered()
    {
        I18n i18n = TestI18n.Full("en");
        MigrationSelectionCandidate locked = Candidate("app#0", "games", "app") with
        {
            Meta = new MigrationItemMeta("app", "app#0", PortabilityClass.ProfileRelative,
                RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, ["process-closed:contoso.exe"])
            {
                ItemOrdinal = 0,
                HasUnanalyzedContent = true,
                ContentProbeStatus = ContentProbeStatus.LockedNow,
            },
            WhatHappensEn = "Recipe-level warning.",
        };
        MigrationSelectionCandidate unlocked = Candidate("app#1", "games", "app") with
        {
            Meta = new MigrationItemMeta("app", "app#1", PortabilityClass.ProfileRelative,
                RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, []) { ItemOrdinal = 1 },
            WhatHappensEn = "Recipe-level warning.",
        };
        MigrationViewModel vm = CreateVm(i18n: i18n);
        vm.LoadScan(Detection(2, 0), @"C:\Users\demo", [locked, unlocked]);
        MigrationAppRow firstOrder = vm.Groups.Single(g => g.Category == MigrationCategory.GameSaves).Apps.Single();

        vm.LoadScan(Detection(2, 0), @"C:\Users\demo",
        [
            locked with { Meta = locked.Meta with { ItemOrdinal = 1 } },
            unlocked with { Meta = unlocked.Meta with { ItemOrdinal = 0 } },
        ]);
        MigrationAppRow reversedOrder = vm.Groups.Single(g => g.Category == MigrationCategory.GameSaves).Apps.Single();

        Assert.Equal("Recipe-level warning.", firstOrder.Subtitle);
        Assert.Equal(firstOrder.Subtitle, reversedOrder.Subtitle);
        Assert.Contains("contoso.exe", firstOrder.Parts.Single(part => part.Candidate.Id == "app#0").WhatHappens);
    }

    [Fact]
    public void App_hides_partial_size_totals_and_uses_a_neutral_multi_source_summary()
    {
        I18n i18n = TestI18n.Full("en");
        MigrationSelectionCandidate known = Candidate("app#0", "games", "app") with
        {
            Meta = new MigrationItemMeta("app", "app#0", PortabilityClass.ProfileRelative,
                RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, []) { ItemOrdinal = 0 },
            SizeBytes = 1024,
            SourcePath = @"C:\Users\demo\one.json",
        };
        MigrationSelectionCandidate unknown = Candidate("app#1", "games", "app") with
        {
            Meta = new MigrationItemMeta("app", "app#1", PortabilityClass.ProfileRelative,
                RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, []) { ItemOrdinal = 1 },
            SizeBytes = null,
            SourcePath = @"C:\Users\demo\two.json",
        };
        MigrationViewModel vm = CreateVm(i18n: i18n);
        vm.LoadScan(Detection(2, 0), @"C:\Users\demo", [known, unknown]);

        MigrationAppRow app = vm.Groups.Single(g => g.Category == MigrationCategory.GameSaves).Apps.Single();

        Assert.Null(app.SizeText);
        Assert.Equal("2 sources", app.SourceSummary);
        Assert.DoesNotContain("KB", app.MetaLine);
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
    public async Task OnNavigatedToAsync_runs_the_scan_and_populates_state()
    {
        MigrationSelectionCandidate candidate = Candidate("project", "projects");
        var scan = new FakeScanService(new MigrationScanResult(
            Detection(1, 0), @"C:\Users\demo", [candidate]));
        MigrationViewModel vm = CreateVm(scan);

        await vm.OnNavigatedToAsync(CancellationToken.None);

        Assert.Equal(1, scan.CallCount);
        Assert.True(vm.IsScanComplete);
        Assert.True(vm.CanSelect);
    }

    [Fact]
    public async Task OnNavigatedToAsync_with_a_precancelled_token_applies_no_state_and_allows_retry()
    {
        var scan = new FakeScanService(new MigrationScanResult(Detection(0, 0), @"C:\Users\demo", []));
        MigrationViewModel vm = CreateVm(scan);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await vm.OnNavigatedToAsync(cts.Token);

        Assert.False(vm.IsScanComplete);            // a cancelled navigation applied no state (P27-R2)
        Assert.Equal(0, scan.CallCount);            // Task.Run never entered Scan on an already-cancelled token

        await vm.OnNavigatedToAsync(CancellationToken.None);   // navigate back / retry
        Assert.True(vm.IsScanComplete);             // the re-entry latch was reset on cancellation
        Assert.Equal(1, scan.CallCount);
    }

    [Fact]
    public async Task Cancellation_between_scan_completion_and_the_queued_UI_apply_applies_no_state_and_allows_retry()
    {
        // Reproduces the exact BLOCKER window: the background scan already finished (result in hand) and the
        // apply callback has been POSTED to the captured UI context, but it has NOT run yet — the shell can
        // still cancel the navigation in that gap (e.g. the user navigated away). A controllable
        // SynchronizationContext lets the test hold the posted callback pending so cancellation can land
        // strictly between "scan work finished" and "the posted apply callback actually running".
        MigrationSelectionCandidate candidate = Candidate("project", "projects");
        var scan = new FakeScanService(new MigrationScanResult(
            Detection(1, 0), @"C:\Users\demo", [candidate]));
        MigrationViewModel vm = CreateVm(scan);

        var context = new QueuingSynchronizationContext();
        using var cts = new CancellationTokenSource();

        Task scanTask;
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            scanTask = vm.StartScanAsync(cts.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        // Wait until the background scan finished and posted the apply callback — but it has not run yet.
        Assert.True(SpinWait.SpinUntil(() => context.PendingCount > 0, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, scan.CallCount);
        Assert.False(vm.IsScanComplete);           // the posted callback has not applied anything yet

        cts.Cancel();                              // the shell's navigate-away cancellation arrives in the gap

        // Drain the queued callback(s) (the apply attempt, then StartScanAsync's own cleanup posts) until the
        // scan task completes. TaskCreationOptions.RunContinuationsAsynchronously means further posts can
        // arrive slightly later than this call, so keep draining until the task is actually done.
        Assert.True(SpinWait.SpinUntil(() =>
        {
            context.RunAll();
            return scanTask.IsCompleted;
        }, TimeSpan.FromSeconds(5)));
        await scanTask;

        Assert.False(vm.IsScanComplete);            // no scan state was applied after the cancelled apply (P27-R2)
        Assert.Empty(vm.Groups);
        Assert.Null(vm.ScanGate);
        Assert.False(vm.IsScanning);

        // Retry must work: a later navigation actually re-runs the scan instead of being suppressed.
        await vm.OnNavigatedToAsync(CancellationToken.None);
        Assert.True(vm.IsScanComplete);
        Assert.Equal(2, scan.CallCount);
    }

    /// <summary>Queues posted callbacks instead of running them, so a test can hold a
    /// <c>SynchronizationContext.Post</c>-ed action pending and control exactly when (and whether) it runs.</summary>
    private sealed class QueuingSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int PendingCount => _queue.Count;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        /// <summary>Runs every callback currently queued (does not wait for new ones to arrive).</summary>
        public void RunAll()
        {
            while (_queue.TryDequeue(out (SendOrPostCallback Callback, object? State) item))
                item.Callback(item.State);
        }
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
    public async Task App_selection_shows_the_full_per_file_runner_plan_before_approval()
    {
        var runner = new RecordingMigrationBackupRunner();
        MigrationRecipe recipe = Recipe("recipe-a", "selected.cfg", "other.cfg");
        MigrationSelectionCandidate selected = Candidate("selected", "projects", "recipe-a");
        MigrationSelectionCandidate optional = Candidate("optional", "projects", "recipe-a") with
        {
            CloudBackup = CloudBackupStatus.BackedUp,
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

    [Theory]
    [InlineData("en", "2 to copy · 1 skipped", "1 copied · 2 failed or skipped")]
    [InlineData("tr", "2 kopyalanacak · 1 atlandı", "1 kopyalandı · 2 başarısız veya atlandı")]
    public async Task Capture_preview_and_mixed_result_use_planned_then_actual_counts(
        string culture, string expectedPreview, string expectedResult)
    {
        var runner = new RecordingMigrationBackupRunner
        {
            PlanSkips = [new RecipeItemSkip("plan-skip", "synthetic plan skip")],
            FailedActionCount = 1,
            FinalizationSkips = [new RecipeItemSkip("finalize-skip", "synthetic finalization skip")],
        };
        MigrationRecipe recipe = Recipe("recipe-a", "first.cfg", "second.cfg");
        MigrationViewModel vm = CreateVm(runner: runner, recipes: [recipe], i18n: TestI18n.Full(culture));
        vm.LoadScan(Detection(1, 0), @"C:\Users\demo", [Candidate("settings", "projects", "recipe-a")]);
        vm.ConfirmProfileCommand.Execute(null);
        vm.PackageDir = OutsideAppPackage();

        await vm.BuildCapturePlanAsync();
        Assert.Equal(expectedPreview, vm.CaptureSummary);

        vm.IsPreviewApproved = true;
        await vm.RunCaptureAsync();

        Assert.Equal(expectedResult, vm.CaptureSummary);
        Assert.Equal(1, vm.CaptureResultRows.Count(row => row.RiskText == "COPIED"));
        Assert.Equal(2, vm.CaptureResultRows.Count(row => row.RiskText == "SKIPPED"));
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
    public async Task PackageDir_inside_an_injected_forbidden_root_is_rejected_before_runner_plan_build()
    {
        var runner = new RecordingMigrationBackupRunner();
        MigrationViewModel vm = CreateCaptureVm(
            runner,
            TestData.PayloadRoots(@"C:\Users\demo\synthetic-app"));
        vm.PackageDir = @"C:\Users\demo\synthetic-app\pkg";

        await vm.BuildCapturePlanAsync();

        Assert.Equal(0, runner.BuildCount);
        Assert.False(vm.HasCapturePlan);
        Assert.Equal("migration.capture.outsideAppWarning", vm.PackageWarning);
    }

    [Fact]
    public async Task A_UNC_package_directory_is_rejected_before_runner_plan_build()
    {
        var runner = new RecordingMigrationBackupRunner();
        MigrationViewModel vm = CreateCaptureVm(
            runner,
            TestData.PayloadRoots(@"C:\Users\demo\synthetic-app"));
        vm.PackageDir = @"\\server\share\pkg";

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
    public void Package_dir_cannot_change_during_an_in_flight_operation()
    {
        MigrationViewModel vm = CreateCaptureVm(new RecordingMigrationBackupRunner());
        string packageDir = vm.PackageDir;
        SetBusy(vm, true);

        Assert.False(vm.CanEditDirectories);
        vm.PackageDir = OutsideAppPackage();

        Assert.Equal(packageDir, vm.PackageDir);
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
            CloudBackup = CloudBackupStatus.NotBackedUp,
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
        I18n? i18n = null,
        PayloadRootPolicy? payloadRoots = null)
        => new(
            i18n ?? new I18n(),
            scan ?? new FakeScanService(new MigrationScanResult(Detection(0, 0), @"C:\Users\demo", [])),
            runner ?? new RecordingMigrationBackupRunner(),
            () => recipes ?? Array.Empty<MigrationRecipe>(),
            payloadRoots ?? TestData.PayloadRoots());

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

    private static MigrationViewModel CreateCaptureVm(
        RecordingMigrationBackupRunner runner,
        PayloadRootPolicy? payloadRoots = null)
    {
        MigrationRecipe recipe = Recipe("recipe-a", "settings.json");
        MigrationViewModel vm = CreateVm(runner: runner, recipes: [recipe], payloadRoots: payloadRoots);
        vm.LoadScan(Detection(1, 0), @"C:\Users\demo", [Candidate("settings", "projects", "recipe-a")]);
        vm.ConfirmProfileCommand.Execute(null);
        vm.PackageDir = OutsideAppPackage();
        return vm;
    }

    private static string OutsideAppPackage()
        => Path.Combine(Path.GetTempPath(), "wck-migration-vm-" + Guid.NewGuid().ToString("N"));

    private static void SetBusy(MigrationViewModel vm, bool value)
    {
        System.Reflection.FieldInfo field = typeof(MigrationViewModel).GetField(
            "_isBusy",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(vm, value);
    }

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
        public IReadOnlyList<RecipeItemSkip> FinalizationSkips { get; init; } = Array.Empty<RecipeItemSkip>();
        public int FailedActionCount { get; init; }
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
                .Select((action, index) => new CopyFileOutcome(
                    action.Id,
                    action.Source,
                    action.Destination,
                    authorized && index >= FailedActionCount,
                    authorized && index >= FailedActionCount ? null : CopySkipReason.Blocked,
                    authorized && index >= FailedActionCount ? "done" : "synthetic failure"))
                .ToArray();
            return new MigrationBackupRunResult(
                authorized,
                new CopySkipReport(outcomes),
                new MigrationRestoreManifest(
                    MigrationRestoreManifest.CurrentSchemaVersion,
                    Array.Empty<MigrationRestoreTarget>()),
                plan.SkippedItems,
                FinalizationSkips);
        }
    }
}

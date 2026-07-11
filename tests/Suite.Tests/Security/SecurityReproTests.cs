using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.App.Theming;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Execution.Adapters;
using WindowsCareKit.Tests.Execution;
using WindowsCareKit.Tests.MigrationRestore;
using WindowsCareKit.Tests.TestInfra;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests.Security;

/// <summary>Host-safe characterizations for destructive-safety findings from the 2026-07-10 review.</summary>
public sealed class DestructivePathSecurityReproTests
{
    private static readonly DateTime T0 = new(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>C1: AppX removal is a gated typed action and its raw adapter is not application-injectable.</summary>
    [Fact]
    public void C1_appx_removal_is_a_typed_action_not_a_directly_injectable_remover()
    {
        var services = new ServiceCollection();
        new UninstallModule().RegisterServices(services);
        using ServiceProvider provider = services.BuildServiceProvider();

        // Fixed: the raw remover is not injectable into view models / application code.
        Assert.Null(provider.GetService<IAppxRemover>());

        // Removal is now a typed PlannedAction that flows through plan hash + gate.
        var action = new AppxRemoveAction
        {
            PackageFullName = "Contoso.App_1.0.0.0_x64__abc",
            PackageDisplayName = "Contoso",
            Description = "remove", Reason = "test",
        };
        Assert.IsAssignableFrom<PlannedAction>(action);
        Assert.Equal("appx.remove", action.Kind);
        var plan = new OperationPlan("t", "uninstall", new PlannedAction[] { action }, T0);
        Assert.False(string.IsNullOrEmpty(plan.ComputeHash()));
        Assert.True(TestData.Gate().Evaluate(action).Allowed);
        Assert.False(TestData.Gate().Evaluate(action with { IsFrameworkOrSystem = true }).Allowed);
        Assert.False(TestData.Gate().Evaluate(action with { PackageFullName = string.Empty }).Allowed);
    }

    /// <summary>S4: the gate blocks a recursive key delete below a protected registry root.</summary>
    [Fact]
    public void S4_protected_registry_descendant_is_blocked_by_the_gate()
    {
        RegistryDeleteAction action = TestData.RegKey(
            RegistryHive.CurrentUser,
            @"SOFTWARE\Policies\Contoso\InjectedPolicy");

        var verdict = TestData.Gate().Evaluate(action);

        Assert.False(verdict.Allowed, verdict.Reason);
    }

    /// <summary>S5: the delete adapter checks only the leaf after the gate and never walks parent components.</summary>
    [Fact]
    public void S5_delete_path_has_a_gate_to_leaf_check_window_without_parent_reparse_validation()
    {
        string executor = RepoSource.Read("src/Suite.Execution/GatedExecutor.cs");
        string adapter = RepoSource.Read("src/Suite.Execution/Adapters/RecycleBinFileDeleteAdapter.cs");

        Assert.Contains("_gate.Evaluate(action)", executor, StringComparison.Ordinal);
        Assert.Contains("_fileAdapter.Delete(file)", executor, StringComparison.Ordinal);
        Assert.Contains("File.GetAttributes(path)", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDirectoryName", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("while (", adapter, StringComparison.Ordinal);
    }

    /// <summary>S8: an asInvoker plan can dispatch a machine-wide service action through an in-process fake adapter.</summary>
    [Fact]
    public void S8_machine_wide_service_action_has_no_per_action_elevation_boundary()
    {
        string manifest = RepoSource.Read("src/Suite.App.Wpf/app.manifest");
        Assert.Contains("requestedExecutionLevel level=\"asInvoker\"", manifest, StringComparison.Ordinal);

        using var fixture = new ExecutorFixture();
        var action = new ServiceDeleteAction
        {
            ServiceName = "ContosoUpdater",
            Operation = ServiceOperation.Delete,
            Description = "delete synthetic service",
            Reason = "security characterization",
        };
        var plan = new OperationPlan("synthetic", "security-repro", [action], T0);

        ExecutionReport report = fixture.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.True(report.Authorized);
        Assert.Equal(ActionStatus.Done, Assert.Single(report.Results).Status);
        Assert.Same(action, Assert.Single(fixture.Adapters.Dispatched));
        Assert.Equal("service:" + action.Id, Assert.Single(fixture.Adapters.Calls));
    }

    /// <summary>S9: ACL hardening catches every exception and backup writing proceeds after the swallowed failure.</summary>
    [Fact]
    public void S9_registry_backup_acl_hardening_is_best_effort_and_fail_open()
    {
        string writer = RepoSource.Read("src/Suite.Execution/Adapters/RegFileBackupWriter.cs");
        int secureCall = writer.IndexOf("CreateSecuredDirectory(dir)", StringComparison.Ordinal);
        int catchAll = writer.IndexOf("catch (Exception)", secureCall, StringComparison.Ordinal);
        int bestEffort = writer.IndexOf("Best-effort hardening", catchAll, StringComparison.Ordinal);
        int writeAfterCall = writer.IndexOf("var sb = new StringBuilder()", secureCall, StringComparison.Ordinal);

        Assert.True(secureCall >= 0);
        Assert.True(catchAll > secureCall);
        Assert.True(bestEffort > catchAll);
        Assert.True(writeAfterCall > secureCall);
        Assert.DoesNotContain("throw;", writer[catchAll..bestEffort], StringComparison.Ordinal);

        string registryAdapter = RepoSource.Read("src/Suite.Execution/Adapters/RegistryDeleteAdapter.cs");
        RepoSource.AssertOrdered(registryAdapter, "_backupWriter.WriteBackup", "RegistryKey.OpenBaseKey");
    }

    /// <summary>S10: restore state uses a predictable sibling .wcktmp and a direct truncating write without link checks.</summary>
    [Fact]
    public void S10_restore_state_staging_path_is_predictable_and_written_directly()
    {
        var store = new RestoreStateStore();
        string stateDir = @"C:\Users\alice\restore-state";
        string target = store.PathFor(stateDir);
        string source = RepoSource.Read("src/Suite.Core/Modules/Install/RestoreStateStore.cs");

        Assert.Equal(RestoreStateStore.FileName, Path.GetFileName(target));
        Assert.Equal(RestoreStateStore.FileName + ".wcktmp", Path.GetFileName(target + ".wcktmp"));
        Assert.Contains("string staging = path + \".wcktmp\"", source, StringComparison.Ordinal);
        Assert.Contains("File.WriteAllText(staging, json)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReparsePoint", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateNew", source, StringComparison.Ordinal);
    }

    /// <summary>S11: changing only service ImagePath leaves the approved plan hash unchanged.</summary>
    [Fact]
    public void S11_service_image_path_is_excluded_from_the_plan_hash_and_not_requeried()
    {
        static ServiceDeleteAction Action(string imagePath) => new()
        {
            ServiceName = "ContosoUpdater",
            Operation = ServiceOperation.Delete,
            ImagePath = imagePath,
            Description = "delete synthetic service",
            Reason = "security characterization",
        };

        var before = new OperationPlan("synthetic", "security-repro", [Action(@"C:\Contoso\updater.exe")], T0);
        var after = new OperationPlan("synthetic", "security-repro", [Action(@"C:\Windows\System32\svchost.exe")], T0);

        Assert.Equal(before.ComputeHash(), after.ComputeHash());

        string adapter = RepoSource.Read("src/Suite.Execution/Adapters/ServiceControlAdapter.cs");
        Assert.Contains("QueryServiceStatus", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("QueryServiceConfig", adapter, StringComparison.Ordinal);
        Assert.DoesNotContain("ImagePath", adapter, StringComparison.Ordinal);
    }

    /// <summary>S-m1: undefined service/task enum values pass the gate and fakes let the executor report Done.</summary>
    [Fact]
    public void S_m1_undefined_service_and_task_operations_are_reported_done_by_the_executor_contract()
    {
        using var fixture = new ExecutorFixture();
        var service = new ServiceDeleteAction
        {
            ServiceName = "ContosoUpdater",
            Operation = (ServiceOperation)999,
            Description = "undefined service operation",
            Reason = "security characterization",
        };
        var task = new TaskDeleteAction
        {
            TaskPath = @"\Contoso\Updater",
            Operation = (TaskOperation)999,
            Description = "undefined task operation",
            Reason = "security characterization",
        };

        Assert.True(fixture.Gate.Evaluate(service).Allowed);
        Assert.True(fixture.Gate.Evaluate(task).Allowed);

        var plan = new OperationPlan("synthetic", "security-repro", [service, task], T0);
        ExecutionReport report = fixture.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.True(report.Authorized);
        Assert.All(report.Results, r => Assert.Equal(ActionStatus.Done, r.Status));
        Assert.Equal(2, fixture.Adapters.Dispatched.Count);
    }
}

/// <summary>Host-safe characterizations for copy, secret scanning, install planning, and backup planning.</summary>
public sealed class CopyAndPlanningSecurityReproTests
{
    private static readonly DateTime T0 = new(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>S1(a): the scanner's bounded prefix can be clean while synthetic token-shaped bytes follow it.</summary>
    [Fact]
    public void S1_bounded_prefix_misses_synthetic_secret_shaped_content_beyond_the_cap()
    {
        byte[] prefix = Encoding.UTF8.GetBytes("benign synthetic configuration");
        byte[] tail = Encoding.UTF8.GetBytes("\nsk-" + new string('Z', 20));
        int tailOffset = EmbeddedSecretScanner.MaxBytesToScan + 1;

        EmbeddedSecretScanResult bounded = EmbeddedSecretScanner.Scan(prefix, "settings.txt");
        EmbeddedSecretScanResult beyondCap = EmbeddedSecretScanner.Scan(tail, "settings.txt");

        Assert.True(tailOffset > EmbeddedSecretScanner.MaxBytesToScan);
        Assert.False(bounded.ContainsSecret);
        Assert.True(beyondCap.ContainsSecret);
        Assert.Contains(
            "new byte[EmbeddedSecretScanner.MaxBytesToScan]",
            RepoSource.Read("src/Suite.Execution/Adapters/CopyAdapter.cs"),
            StringComparison.Ordinal);
    }

    /// <summary>S1(b): an unquoted synthetic key/value is outside the scanner's recognized key/value format.</summary>
    [Fact]
    public void S1_unquoted_synthetic_secret_assignment_is_not_detected()
    {
        byte[] content = Encoding.UTF8.GetBytes("api_key = synthetic_value");

        EmbeddedSecretScanResult result = EmbeddedSecretScanner.Scan(content, "settings.env");

        Assert.False(result.ContainsSecret);
    }

    /// <summary>S1(a): scan and copy reopen the path separately with write/delete sharing, proving the TOCTOU window.</summary>
    [Fact]
    public void S1_copy_scans_then_reopens_the_mutable_path_for_copy()
    {
        string copy = RepoSource.Read("src/Suite.Execution/Adapters/CopyAdapter.cs");
        int scan = copy.IndexOf("ScanForEmbeddedSecret(source)", StringComparison.Ordinal);
        int copyCall = copy.IndexOf("File.Copy(LongPath(source), LongPath(destination)", scan, StringComparison.Ordinal);
        int open = copy.IndexOf("new FileStream(", copy.IndexOf("ScanForEmbeddedSecret", StringComparison.Ordinal), StringComparison.Ordinal);
        int permissiveShare = copy.IndexOf("FileShare.ReadWrite | FileShare.Delete", open, StringComparison.Ordinal);

        Assert.True(scan >= 0);
        Assert.True(copyCall > scan);
        Assert.True(open > copyCall);
        Assert.True(permissiveShare > open);
    }

    /// <summary>S3: untrusted RequiresAdmin metadata must never become an elevated winget action.</summary>
    [Fact]
    public void S3_requires_admin_winget_metadata_is_manual_only_never_auto_elevated()
    {
        var entry = new InstallEntry(
            "crafted", "install", "tool", InstallMethod.Winget,
            "Contoso.Tool", null, RequiresAdmin: true, RebootExpected: false,
            RestoreOrder: 10, Description: "synthetic")
        {
            InstallTier = InstallTier.Auto,
        };
        var planner = new InstallPlanner(TestData.Gate(), new FakeDriverGuard());

        InstallPlanResult result = planner.BuildPlan(new InstallManifest([entry]), RestoreState.Empty, T0);

        // Fixed: a RequiresAdmin winget entry no longer becomes an auto-run elevated action.
        Assert.Empty(result.Plan.Actions);
        InstallSkip skip = Assert.Single(result.Skipped);
        Assert.Equal(InstallSkipReason.RequiresAdminManual, skip.Reason);
        Assert.Contains(result.ManualChecklist, e => e.Id == entry.Id);

        string plannerSource = RepoSource.Read("src/Suite.Core/Modules/Install/InstallPlanner.cs");
        Assert.Contains("Environment.SpecialFolder.ApplicationData", plannerSource, StringComparison.Ordinal);
        Assert.Contains("\"npm\", \"npm.cmd\"", plannerSource, StringComparison.Ordinal);
        Assert.Contains("InstallSkipReason.RequiresAdminManual", plannerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RequiresElevation = entry.RequiresAdmin", plannerSource, StringComparison.Ordinal);
    }

    /// <summary>S6: a traversal target canonicalizes outside the selected payload root and is still planned.</summary>
    [Fact]
    public void S6_backup_target_traversal_escapes_the_payload_root()
    {
        string payload = @"C:\Users\alice\wck-repro\payload";
        BackupEntry entry = CopyEntry(@"C:\Users\alice\source", @"..\victim.txt");

        BackupPlanResult result = Planner().BuildPlan(new BackupManifest([entry]), payload, T0);
        CopyAction action = Assert.IsType<CopyAction>(Assert.Single(result.Plan.Actions));

        string normalizedPayload = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payload));
        Assert.False(action.Destination.StartsWith(
            normalizedPayload + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.GetFullPath(@"C:\Users\alice\wck-repro\victim.txt"), action.Destination);
    }

    /// <summary>S6: the planner accepts a payload inside the copied source, producing a recursive-growth shape.</summary>
    [Fact]
    public void S6_destination_inside_source_is_accepted_by_the_planner()
    {
        string source = @"C:\Users\alice\wck-source";
        string payload = Path.Combine(source, "payload");

        BackupPlanResult result = Planner().BuildPlan(
            new BackupManifest([CopyEntry(source, "nested")]),
            payload,
            T0);
        CopyAction action = Assert.IsType<CopyAction>(Assert.Single(result.Plan.Actions));

        Assert.StartsWith(
            Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar,
            action.Destination,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.Skipped, s => s.Reason.Contains("source", StringComparison.OrdinalIgnoreCase));
    }

    private static BackupPlanner Planner() => new(TestData.Gate(), new FakeEnvironmentExpander());

    private static BackupEntry CopyEntry(string source, string target) => new(
        "security-repro", true, BackupMethod.Copy, "synthetic", source, target,
        Array.Empty<string>(), SecretHandling.Normal, 1, "merge-after-install", "synthetic", null);
}

/// <summary>Deterministic UI/reliability characterizations that never open an application window.</summary>
public sealed class UiReliabilitySecurityReproTests
{
    /// <summary>G2: two gated overlapping loads deterministically append the same installed-app row twice.</summary>
    [Fact]
    public async Task G2_overlapping_uninstall_loads_duplicate_rows()
    {
        var reader = new BlockingInstalledAppReader(
            [TestData.App(displayName: "Duplicated App", regKeyName: "duplicate")]);
        UninstallViewModel vm = BuildUninstallVm(reader, new EmptyAppxReader());

        Task loadA = vm.LoadAsync();
        Assert.True(reader.FirstCallEntered.Wait(TimeSpan.FromSeconds(10)));

        Task loadB = vm.LoadAsync();
        await loadB.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(vm.AllRows);

        reader.ReleaseFirstCall.Set();
        await loadA.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, vm.AllRows.Count(r => r.DisplayName == "Duplicated App"));
    }

    /// <summary>G3: a RelayCommand async lambda returns before completion and posts its post-await exception.</summary>
    [Fact]
    public async Task G3_relay_command_cannot_return_or_observe_the_async_callback_task()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new ExceptionCapturingSynchronizationContext();
        SynchronizationContext? prior = SynchronizationContext.Current;

        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var command = new RelayCommand(async () =>
            {
                entered.SetResult();
                await release.Task;
                throw new InvalidOperationException("synthetic post-await failure");
            });

            command.Execute(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(context.Exception.Task.IsCompleted);

            release.SetResult();
            Exception observed = await context.Exception.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("synthetic post-await failure", observed.Message);
            Assert.Equal(typeof(void), typeof(RelayCommand).GetMethod(nameof(RelayCommand.Execute))!.ReturnType);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prior);
        }
    }

    /// <summary>G4: changing PayloadDir during a blocked build lets the completed old plan resurrect.</summary>
    [Fact]
    public async Task G4_backup_build_resurrects_a_plan_for_the_old_payload_path()
    {
        string source = @"C:\Users\alice\AppData\Roaming\Contoso";
        string payloadA = @"C:\Users\alice\backup-a";
        string payloadB = @"C:\Users\alice\backup-b";
        var loader = new BlockingManifestLoader(CopyEntry(source, "contoso"));
        var vm = new BackupViewModel(
            TestI18n.Full("en"),
            loader,
            new BackupPlanner(TestData.Gate(), new FakeEnvironmentExpander()),
            null!)
        {
            PayloadDir = payloadA,
        };

        Task build = vm.BuildPlanAsync();
        Assert.True(loader.Entered.Wait(TimeSpan.FromSeconds(10)));
        vm.PayloadDir = payloadB;
        Assert.False(vm.HasPlan);

        loader.Release.Set();
        await build.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(payloadB, vm.PayloadDir);
        Assert.True(vm.HasPlan);
        string preview = Assert.Single(vm.PlanRows).Text;
        Assert.Contains(payloadA, preview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(payloadB, preview, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>G-m3: an injected swallowed probe failure renders exactly like a legitimate empty inventory.</summary>
    [Fact]
    public async Task G_m3_swallowed_probe_failure_has_no_unavailable_or_diagnostic_state()
    {
        var probe = new ThrowingRegistryProbe();
        var installed = new Win32InstalledAppReader(probe);
        UninstallViewModel vm = BuildUninstallVm(installed, new EmptyAppxReader());

        await vm.LoadAsync();

        Assert.Equal(3, probe.CallCount);
        Assert.Empty(vm.AllRows);
        Assert.False(vm.IsLoading);
        string[] diagnosticProperties = typeof(UninstallViewModel)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => p.Name)
            .Where(n => n.Contains("Error", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Unavailable", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(diagnosticProperties);

        string installedSource = RepoSource.Read("src/Suite.Win32/Win32InstalledAppReader.cs");
        string appxSource = RepoSource.Read("src/Suite.Win32/Win32AppxReader.cs");
        Assert.Contains("catch (Exception", installedSource, StringComparison.Ordinal);
        Assert.Contains("catch (Exception", appxSource, StringComparison.Ordinal);
    }

    /// <summary>G-m4: a missing WPF binding path reaches PathError while measure/arrange completes normally.</summary>
    [Fact]
    public void G_m4_render_only_smoke_test_does_not_throw_for_a_broken_binding()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var text = new TextBlock { DataContext = new BrokenBindingSource() };
                text.SetBinding(TextBlock.TextProperty, new Binding("DefinitelyMissing.Value"));
                text.Measure(new Size(200, 40));
                text.Arrange(new Rect(0, 0, 200, 40));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

                BindingExpression expression = Assert.IsType<BindingExpression>(
                    BindingOperations.GetBindingExpression(text, TextBlock.TextProperty));
                Assert.Equal(BindingStatus.PathError, expression.Status);
                Assert.Equal(string.Empty, text.Text);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        string smoke = RepoSource.Read("tests/Suite.Tests/ViewRenderSmokeTests.cs");
        Assert.DoesNotContain("PresentationTraceSources", smoke, StringComparison.Ordinal);
    }

    private static UninstallViewModel BuildUninstallVm(IInstalledAppReader installed, IAppxReader appx) => new(
        TestI18n.Full("en"), installed, appx, TestData.Gate(), new FakeLeftoverProbe(),
        new NoOpExecutor(), new NoOpFolderOpener());

    private static BackupEntry CopyEntry(string source, string target) => new(
        "security-repro", true, BackupMethod.Copy, "synthetic", source, target,
        Array.Empty<string>(), SecretHandling.Normal, 1, "merge-after-install", "synthetic", null);

    private sealed class BlockingInstalledAppReader(IReadOnlyList<InstalledApp> apps) : IInstalledAppReader
    {
        private int _calls;
        public ManualResetEventSlim FirstCallEntered { get; } = new();
        public ManualResetEventSlim ReleaseFirstCall { get; } = new();

        public IReadOnlyList<InstalledApp> ReadAll()
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstCallEntered.Set();
                if (!ReleaseFirstCall.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("first synthetic inventory read was not released");
            }
            return apps;
        }
    }

    private sealed class BlockingManifestLoader(BackupEntry entry) : IManifestLoader
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public BackupManifest LoadFromDirectory(string manifestsDirectory)
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("synthetic manifest load was not released");
            return new BackupManifest([entry]);
        }

        public BackupManifest LoadFromJson(IEnumerable<string> jsonDocuments) => new([entry]);
    }

    private sealed class ExceptionCapturingSynchronizationContext : SynchronizationContext
    {
        public TaskCompletionSource<Exception> Exception { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback d, object? state)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SynchronizationContext? prior = Current;
                SetSynchronizationContext(this);
                try
                {
                    d(state);
                }
                catch (Exception ex)
                {
                    Exception.TrySetResult(ex);
                }
                finally
                {
                    SetSynchronizationContext(prior);
                }
            });
        }
    }

    private sealed class ThrowingRegistryProbe : IRegistryProbe
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, RegistryView view, string subKey)
        {
            CallCount++;
            throw new UnauthorizedAccessException("synthetic probe failure");
        }

        public RegistryKeySnapshot? ReadKey(RegistryHive hive, RegistryView view, string subKey)
            => throw new InvalidOperationException("entry reads must not occur when enumeration failed");
    }

    private sealed class EmptyAppxReader : IAppxReader
    {
        public IReadOnlyList<InstalledAppx> ReadCurrentUserPackages() => Array.Empty<InstalledAppx>();
    }

    private sealed class NoOpExecutor : IExecutor
    {
        public ExecutionOutcome Execute(OperationPlan plan, string approvedPlanHash) => new(true, "not used");
    }

    private sealed class NoOpFolderOpener : IFolderOpener
    {
        public void OpenFolder(string path) { }
    }

    private sealed class BrokenBindingSource;
}

/// <summary>Host-safe closure proofs for the findings carried into the 2026-07-11 continuation pass.</summary>
public sealed class SecurityReproPart2Tests
{
    private static readonly DateTime T0 = new(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>S7: a real restore service reports failed and never-run targets as NotRestored.</summary>
    [Fact]
    public void S7_failed_and_not_run_restore_actions_are_reported_not_restored()
    {
        var copy = new FailOnSecondMergeAdapter();
        using var fixture = new RestoreServiceFixture(copy);
        MigrationRestorePreviewResult preview = fixture.Service.Preview(
            fixture.Manifest, fixture.PackageDirectory, fixture.StateDirectory, T0);

        MigrationRestoreExecutionResult result = fixture.Service.Restore(
            fixture.Manifest,
            fixture.PackageDirectory,
            fixture.StateDirectory,
            T0,
            runToken: "failure-mix",
            approvedHash: preview.PlanHash);

        Assert.Equal(
            new[] { ActionStatus.Done, ActionStatus.Failed, ActionStatus.NotRun },
            result.Execution.Results.Select(r => r.Status).ToArray());
        Assert.False(result.Execution.AllDone);
        Assert.Equal(2, copy.MergeCalls);
        AssertRestoreReportMatchesExecution(result);

        Assert.Equal(RestoreEntryStatus.Done, StatusForResult(result, 0));
        Assert.Equal(RestoreEntryStatus.Failed, StatusForResult(result, 1));
        Assert.Equal(RestoreEntryStatus.Pending, StatusForResult(result, 2));
    }

    /// <summary>S7: an execution-time SafetyGate refusal and the following NotRun target are NotRestored.</summary>
    [Fact]
    public void S7_execution_time_refusal_and_not_run_restore_actions_are_reported_not_restored()
    {
        var copy = new RecordingMergeAdapter();
        BlockSecondExecutionGate? blockingGate = null;
        using var fixture = new RestoreServiceFixture(copy, inner => blockingGate = new BlockSecondExecutionGate(inner));
        MigrationRestorePreviewResult preview = fixture.Service.Preview(
            fixture.Manifest, fixture.PackageDirectory, fixture.StateDirectory, T0);
        blockingGate!.Reset();

        MigrationRestoreExecutionResult result = fixture.Service.Restore(
            fixture.Manifest,
            fixture.PackageDirectory,
            fixture.StateDirectory,
            T0,
            runToken: "refusal-mix",
            approvedHash: preview.PlanHash);

        Assert.Equal(
            new[] { ActionStatus.Done, ActionStatus.Blocked, ActionStatus.NotRun },
            result.Execution.Results.Select(r => r.Status).ToArray());
        Assert.False(result.Execution.AllDone);
        Assert.Equal(1, copy.MergeCalls);
        AssertRestoreReportMatchesExecution(result);

        Assert.Equal(RestoreEntryStatus.Done, StatusForResult(result, 0));
        Assert.Equal(RestoreEntryStatus.Failed, StatusForResult(result, 1));
        Assert.Equal(RestoreEntryStatus.Pending, StatusForResult(result, 2));
    }

    /// <summary>S2: rejection does not unregister a module directory from the lifetime-global dependency resolver.</summary>
    [Fact]
    public void S2_rejected_module_directory_remains_a_global_dependency_probe()
    {
        using var workspace = new TempWorkspace("wck-s2-");
        string rejectedRoot = workspace.Combine("rejected-root");
        string rejectedDirectory = Path.Combine(rejectedRoot, "rejected");
        Directory.CreateDirectory(rejectedDirectory);

        // Force this real module into the default context first. Catalog loads of byte-identical copies then
        // de-duplicate to it instead of pinning a disposable fixture DLL for the process lifetime.
        Assembly knownModule = typeof(UninstallModule).Assembly;
        File.Copy(
            knownModule.Location,
            Path.Combine(rejectedDirectory, "Suite.Module.rejected.dll"));

        const string originalDependencyName = "Suite.Module.Restore";
        const string syntheticDependencyName = "Suite.Module.Reproxx";
        string dependencySource = Path.Combine(
            AppContext.BaseDirectory, "Modules", "restore", originalDependencyName + ".dll");
        Assert.True(File.Exists(dependencySource), $"missing fixture dependency: {dependencySource}");
        string rejectedDependency = Path.Combine(rejectedDirectory, syntheticDependencyName + ".dll");
        CopyWithRenamedAssemblyIdentity(
            dependencySource, rejectedDependency, originalDependencyName, syntheticDependencyName);
        Assert.Equal(syntheticDependencyName, AssemblyName.GetAssemblyName(rejectedDependency).Name);

        var rejectedCatalog = new DirectoryModuleCatalog(rejectedRoot);
        IReadOnlyList<IWckModule> rejectedModules = rejectedCatalog.LoadModules();
        Assert.Equal(new[] { "settings" }, rejectedModules.Select(m => m.Id).ToArray());
        Assert.Contains(
            rejectedCatalog.Diagnostics,
            d => d.Contains("module id does not match", StringComparison.OrdinalIgnoreCase));

        string validRoot = workspace.Combine("valid-root");
        string validDirectory = Path.Combine(validRoot, "uninstall");
        Directory.CreateDirectory(validDirectory);
        File.Copy(knownModule.Location, Path.Combine(validDirectory, "Suite.Module.uninstall.dll"));
        var validCatalog = new DirectoryModuleCatalog(validRoot);
        Assert.Contains(validCatalog.LoadModules(), module => module.Id == "uninstall");

        Type resolver = typeof(DirectoryModuleCatalog).Assembly.GetType(
            "WindowsCareKit.App.Modules.ModuleAssemblyResolver",
            throwOnError: true)!;
        MethodInfo handler = resolver.GetMethod("OnResolving", BindingFlags.Static | BindingFlags.NonPublic)!;
        string? resolvedLocation = ResolveInCollectibleContext(handler, syntheticDependencyName);

        Assert.Equal(Path.GetFullPath(rejectedDependency), Path.GetFullPath(resolvedLocation!));
        Assert.False(File.Exists(Path.Combine(validDirectory, syntheticDependencyName + ".dll")));
    }

    /// <summary>G1: literal Turkish keyboard input is rejected when the resource culture is tr but process culture is en-US.</summary>
    [Fact]
    public void G1_turkish_confirmation_uses_process_culture_instead_of_selected_ui_culture()
    {
        CultureInfo priorCulture = CultureInfo.CurrentCulture;
        CultureInfo priorUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            I18n i18n = ShellI18n("tr");
            var gate = new ConfirmGateViewModel(i18n, () => { }, () => { }, () => false);
            gate.Open(ConfirmTier.Irreversible, "title", "body", Array.Empty<PlanRow>());

            gate.TypedConfirm = "sil"; // literal normal Turkish keyboard input; not derived by ToLowerInvariant

            Assert.Equal("tr", i18n.SelectedCulture);
            Assert.Equal("en-US", CultureInfo.CurrentCulture.Name);
            Assert.Equal("SİL", gate.ConfirmWord);
            Assert.False(gate.TypedMatches);
            Assert.False(gate.CanApprove);
            Assert.Equal(
                0,
                CultureInfo.GetCultureInfo("tr-TR").CompareInfo.Compare(
                    gate.TypedConfirm,
                    gate.ConfirmWord,
                    CompareOptions.IgnoreCase));
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
            CultureInfo.CurrentUICulture = priorUiCulture;
        }
    }

    /// <summary>G6: production-style bindings keep PlanRow's English text and frozen dark-palette brush after switches.</summary>
    [Fact]
    public void G6_plan_row_display_stays_english_and_dark_palette_after_language_and_theme_switch()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                I18n i18n = ShellI18n("en");
                CopyAction action = TestData.Copy(@"C:\source\settings.json", @"D:\backup\settings.json") with
                {
                    Risk = RiskLevel.Medium,
                    Undo = UndoCapability.Partial,
                };
                PlanRow row = PlanRow.FromAction(action, isWholeTree: true);

                var host = new StackPanel();
                host.Resources.MergedDictionaries.Add(LoadTheme("Strongbox"));
                var actionText = BoundText(row, nameof(PlanRow.Text));
                var riskText = BoundText(row, nameof(PlanRow.RiskText));
                riskText.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(PlanRow.RiskBrush)));
                var undoText = BoundText(row, nameof(PlanRow.Undo));
                host.Children.Add(actionText);
                host.Children.Add(riskText);
                host.Children.Add(undoText);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

                string beforeActionText = actionText.Text;
                string beforeRiskText = riskText.Text;
                string beforeUndoText = undoText.Text;
                Color beforeDisplayedColor = Assert.IsType<SolidColorBrush>(riskText.Foreground).Color;

                i18n.SelectedCulture = "tr";
                host.Resources.MergedDictionaries[0] = LoadTheme("Daylight");
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Color daylightMedium = Assert.IsType<SolidColorBrush>(host.FindResource("Backup.MedFg")).Color;

                Assert.Equal("tr", i18n.SelectedCulture);
                Assert.Equal(beforeActionText, actionText.Text);
                Assert.StartsWith("Copy: ", actionText.Text, StringComparison.Ordinal);
                Assert.Contains("(whole-tree copy)", row.Detail, StringComparison.Ordinal);
                Assert.Equal(beforeRiskText, riskText.Text);
                Assert.Equal("Medium", riskText.Text);
                Assert.Equal(beforeUndoText, undoText.Text);
                Assert.Equal("undo: Partial", undoText.Text);
                Assert.Equal(beforeDisplayedColor, Assert.IsType<SolidColorBrush>(riskText.Foreground).Color);
                Assert.NotEqual(daylightMedium, beforeDisplayedColor);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>G-m2: preview and successful result branches call the same future/ambiguous localization keys.</summary>
    [Fact]
    public void G_m2_preview_and_result_summaries_reuse_the_same_ambiguous_copy()
    {
        string backup = RepoSource.Read("src/Suite.Module.Backup/ViewModels/BackupViewModel.cs");
        string migration = RepoSource.Read("src/Suite.Module.Migration/ViewModels/MigrationViewModel.cs");
        string backupEnglish = RepoSource.Read("src/Suite.Module.Backup/lang/en.json");
        string migrationEnglish = RepoSource.Read("src/Suite.Module.Migration/lang/en.json");

        Assert.Equal(2, CountOccurrences(backup, "I18n.Format(\"backup.report.summaryShort\""));
        Assert.Equal(2, CountOccurrences(migration, "\"migration.capture.resultSummary\""));
        Assert.Contains("{0} to copy", backupEnglish, StringComparison.Ordinal);
        Assert.Contains("{0} copied or planned", migrationEnglish, StringComparison.Ordinal);
    }

    /// <summary>G-m5: both launch ports are void and their production lambdas drop Process.Start's handle.</summary>
    [Fact]
    public void G_m5_url_and_folder_openers_discard_process_handles()
    {
        Assert.Equal(typeof(void), typeof(IUrlOpener).GetMethod(nameof(IUrlOpener.Open))!.ReturnType);
        Assert.Equal(typeof(void), typeof(IFolderOpener).GetMethod(nameof(IFolderOpener.OpenFolder))!.ReturnType);

        string url = RepoSource.Read("src/Suite.Execution/Adapters/UrlOpener.cs");
        string folder = RepoSource.Read("src/Suite.Execution/Adapters/FolderOpener.cs");
        Assert.Contains("private readonly Action<ProcessStartInfo> _launch", url, StringComparison.Ordinal);
        Assert.Contains("private readonly Action<ProcessStartInfo> _launch", folder, StringComparison.Ordinal);
        Assert.Contains("psi => Process.Start(psi)", url, StringComparison.Ordinal);
        Assert.Contains("psi => Process.Start(psi)", folder, StringComparison.Ordinal);
        Assert.DoesNotContain("using Process", url, StringComparison.Ordinal);
        Assert.DoesNotContain("using Process", folder, StringComparison.Ordinal);
        Assert.DoesNotContain(".Dispose()", url, StringComparison.Ordinal);
        Assert.DoesNotContain(".Dispose()", folder, StringComparison.Ordinal);
    }

    /// <summary>G-m6: the real uninstall view filters out İstanbul for a Turkish user's literal "istanbul" query.</summary>
    [Fact]
    public async Task G_m6_uninstall_search_fails_turkish_dotted_i_case_matching()
    {
        CultureInfo priorCulture = CultureInfo.CurrentCulture;
        CultureInfo priorUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo turkish = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentCulture = turkish;
            CultureInfo.CurrentUICulture = turkish;
            var reader = new StaticInstalledAppReader([
                TestData.App(displayName: "İstanbul Araçları", source: InstalledAppSource.CurrentUser),
            ]);
            var vm = new UninstallViewModel(
                ShellI18n("tr"),
                reader,
                new EmptyAppxReader(),
                TestData.Gate(),
                new FakeLeftoverProbe(),
                new NoOpExecutor(),
                new NoOpFolderOpener());

            await vm.LoadAsync();
            AppRow row = Assert.Single(vm.AllRows);
            vm.Search = "istanbul";

            Assert.Equal("tr", vm.I18n.SelectedCulture);
            Assert.DoesNotContain("istanbul", row.SearchKey, StringComparison.Ordinal);
            Assert.Empty(vm.AppsView.Cast<AppRow>());
            Assert.True(
                turkish.CompareInfo.IndexOf(row.DisplayName, "istanbul", CompareOptions.IgnoreCase) >= 0);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
            CultureInfo.CurrentUICulture = priorUiCulture;
        }
    }

    private static void AssertRestoreReportMatchesExecution(MigrationRestoreExecutionResult result)
    {
        // The one Done action's entry is the ONLY entry reported Restored; the other two are NotRestored.
        var doneEntryIds = result.Execution.Results
            .Where(r => r.Status == ActionStatus.Done)
            .Select(r => result.PlanResult.ActionEntryIds[r.ActionId])
            .Order(StringComparer.Ordinal).ToArray();
        var notDoneEntryIds = result.Execution.Results
            .Where(r => r.Status != ActionStatus.Done)
            .Select(r => result.PlanResult.ActionEntryIds[r.ActionId])
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(doneEntryIds,
            result.RestoreReport.Restored.Select(e => e.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.All(result.RestoreReport.Restored, e => Assert.Equal(RestoreDisposition.Restored, e.Disposition));

        Assert.Equal(notDoneEntryIds,
            result.RestoreReport.NotRestored.Select(e => e.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.All(result.RestoreReport.NotRestored, e => Assert.Equal(RestoreDisposition.NotRestored, e.Disposition));
    }

    private static RestoreEntryStatus StatusForResult(MigrationRestoreExecutionResult result, int resultIndex)
    {
        ActionResult actionResult = result.Execution.Results[resultIndex];
        string entryId = result.PlanResult.ActionEntryIds[actionResult.ActionId];
        return result.State.StatusOf(entryId);
    }

    private static string? ResolveInCollectibleContext(MethodInfo handler, string dependencyName)
    {
        AssemblyLoadContext? context = new(
            "wck-s2-repro-" + Guid.NewGuid().ToString("N"),
            isCollectible: true);
        Assembly? resolved = null;
        try
        {
            resolved = handler.Invoke(
                null,
                new object?[] { context, new AssemblyName(dependencyName) }) as Assembly;
            Assert.NotNull(resolved);
            Assert.Equal(dependencyName, resolved.GetName().Name);
            return resolved.Location;
        }
        finally
        {
            resolved = null;
            context.Unload();
            context = null;
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }

    private static void CopyWithRenamedAssemblyIdentity(
        string source,
        string destination,
        string originalName,
        string replacementName)
    {
        byte[] original = Encoding.UTF8.GetBytes(originalName + "\0");
        byte[] replacement = Encoding.UTF8.GetBytes(replacementName + "\0");
        Assert.Equal(original.Length, replacement.Length);

        byte[] image = File.ReadAllBytes(source);
        int replacements = 0;
        for (int i = 0; i <= image.Length - original.Length; i++)
        {
            if (!image.AsSpan(i, original.Length).SequenceEqual(original))
                continue;

            replacement.CopyTo(image, i);
            replacements++;
            i += original.Length - 1;
        }

        Assert.True(replacements > 0, $"assembly identity marker not found: {originalName}");
        File.WriteAllBytes(destination, image);
    }

    private static I18n ShellI18n(string culture)
    {
        var i18n = new I18n(RepoSource.PathFor("src/Suite.App.Wpf/lang"));
        i18n.Load(culture);
        return i18n;
    }

    private static ResourceDictionary LoadTheme(string themeName)
        => new()
        {
            Source = new Uri(
                $"pack://application:,,,/WindowsCareKit;component/Themes/{themeName}.xaml",
                UriKind.Absolute),
        };

    private static TextBlock BoundText(object dataContext, string property)
    {
        var text = new TextBlock { DataContext = dataContext };
        text.SetBinding(TextBlock.TextProperty, new Binding(property));
        return text;
    }

    private static int CountOccurrences(string text, string marker)
        => text.Split(marker, StringSplitOptions.None).Length - 1;

    private sealed class RestoreServiceFixture : IDisposable
    {
        private readonly TempWorkspace _workspace = new("wck-s7-");

        public RestoreServiceFixture(
            ICopyAdapter copyAdapter,
            Func<ISafetyGate, ISafetyGate>? wrapGate = null)
        {
            PackageDirectory = _workspace.Combine("package");
            StateDirectory = _workspace.Combine("state");
            string usersRoot = _workspace.Combine("Users");
            string profile = Path.Combine(usersRoot, "synthetic-user");
            Directory.CreateDirectory(profile);
            Directory.CreateDirectory(StateDirectory);

            string payloadDirectory = Path.Combine(PackageDirectory, "migration", "security-repro");
            Directory.CreateDirectory(payloadDirectory);
            for (int i = 1; i <= 3; i++)
                File.WriteAllText(Path.Combine(payloadDirectory, $"source-{i}.json"), $"synthetic-{i}");

            Manifest = new MigrationRestoreManifest(1, Enumerable.Range(1, 3).Select(i =>
                new MigrationRestoreTarget(
                    $"security.recipe.{i}",
                    $"security-entry-{i}",
                    KnownFolder.UserProfile,
                    $"target-{i}.json",
                    $"migration/security-repro/source-{i}.json",
                    RestoreStrategy.ConfigWrite,
                    RestorePhase.ConfigWrite,
                    Array.Empty<string>(),
                    PortabilityClass.ProfileRelative,
                    $"synthetic-sha-{i}")
                {
                    RestoreTier = RestoreTier.ConfigCopy,
                }).ToArray());

            SafetyGate innerGate = MigrationRestoreTestData.GateForProfile(profile, usersRoot);
            ISafetyGate gate = wrapGate?.Invoke(innerGate) ?? innerGate;
            var runner = new MigrationRestoreRunner(
                new RecipePathResolver(new ProfileRoots(
                    profile,
                    Path.Combine(profile, "AppData", "Roaming"),
                    Path.Combine(profile, "AppData", "Local"))),
                gate);
            var unused = new RecordingAdapters { ThrowOnAnyCall = true };
            var executor = new GatedExecutor(
                gate,
                new ExecutionLog(
                    _workspace.Combine("execution.jsonl"),
                    new LogRedactor(null, null)),
                unused.File,
                unused.Registry,
                unused.Service,
                unused.Task,
                unused.Process,
                copyAdapter);
            Service = new MigrationRestoreService(runner, executor, new RestoreStateStore());
        }

        public string PackageDirectory { get; }
        public string StateDirectory { get; }
        public MigrationRestoreManifest Manifest { get; }
        public MigrationRestoreService Service { get; }

        public void Dispose() => _workspace.Dispose();
    }

    private sealed class FailOnSecondMergeAdapter : ICopyAdapter
    {
        public int MergeCalls { get; private set; }

        public CopyAdapterResult Copy(CopyAction action)
            => throw new InvalidOperationException("copy actions are not expected in a restore repro");

        public void Merge(RestoreMergeAction action)
        {
            MergeCalls++;
            if (MergeCalls == 2)
                throw new IOException("synthetic second-merge failure");
        }
    }

    private sealed class RecordingMergeAdapter : ICopyAdapter
    {
        public int MergeCalls { get; private set; }

        public CopyAdapterResult Copy(CopyAction action)
            => throw new InvalidOperationException("copy actions are not expected in a restore repro");

        public void Merge(RestoreMergeAction action) => MergeCalls++;
    }

    private sealed class BlockSecondExecutionGate(ISafetyGate inner) : ISafetyGate
    {
        private int _evaluateCalls;

        public SafetyVerdict Evaluate(PlannedAction action)
        {
            int call = Interlocked.Increment(ref _evaluateCalls);
            return call == 5
                ? SafetyVerdict.Block("synthetic execution-time refusal")
                : inner.Evaluate(action);
        }

        public PlanValidationResult Validate(OperationPlan plan) => inner.Validate(plan);

        public void Reset() => Volatile.Write(ref _evaluateCalls, 0);
    }

    private sealed class StaticInstalledAppReader(IReadOnlyList<InstalledApp> apps) : IInstalledAppReader
    {
        public IReadOnlyList<InstalledApp> ReadAll() => apps;
    }

    private sealed class EmptyAppxReader : IAppxReader
    {
        public IReadOnlyList<InstalledAppx> ReadCurrentUserPackages() => Array.Empty<InstalledAppx>();
    }

    private sealed class NoOpExecutor : IExecutor
    {
        public ExecutionOutcome Execute(OperationPlan plan, string approvedPlanHash) => new(true, "not used");
    }

    private sealed class NoOpFolderOpener : IFolderOpener
    {
        public void OpenFolder(string path) { }
    }
}

/// <summary>Static closure proofs for the review's low-confidence repository/process observations.</summary>
public sealed class LowConfidenceSecurityReproTests
{
    /// <summary>§6 community wiring: production migration DI uses only BuiltinRecipeSource, not CommunityRecipeSource.</summary>
    [Fact]
    public void Community_recipe_source_is_not_wired_into_the_active_catalog()
    {
        string module = RepoSource.Read("src/Suite.Module.Migration/MigrationModule.cs");
        Assert.Contains("BuiltinRecipeSource.LoadAll", module, StringComparison.Ordinal);
        Assert.DoesNotContain("CommunityRecipeSource", module, StringComparison.Ordinal);
    }

    /// <summary>§6 release inputs: installer language is implicit and release tags are not checked against VersionPrefix.</summary>
    [Fact]
    public void Installer_language_and_release_tag_version_sync_are_not_enforced()
    {
        string installer = RepoSource.Read("installer/WindowsCareKit.iss");
        string release = RepoSource.Read(".github/workflows/release.yml");

        Assert.DoesNotContain("[Languages]", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$appVer = $refName.TrimStart('v')", release, StringComparison.Ordinal);
        Assert.DoesNotContain("VersionPrefix", release, StringComparison.Ordinal);
    }

    /// <summary>§6 destination race: reparse validation and File.Copy remain separate non-atomic operations.</summary>
    [Fact]
    public void Destination_reparse_check_and_copy_are_not_handle_bound()
    {
        string copy = RepoSource.Read("src/Suite.Execution/Adapters/CopyAdapter.cs");
        RepoSource.AssertOrdered(copy, "GuardDestinationNotReparse(destination)", "File.Copy(LongPath(source), LongPath(destination)");
        Assert.DoesNotContain("SafeFileHandle", copy, StringComparison.Ordinal);
    }
}

internal static class RepoSource
{
    private static readonly Lazy<string> Root = new(FindRoot);

    public static string PathFor(string relativePath)
        => Path.Combine(Root.Value, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public static string Read(string relativePath)
        => File.ReadAllText(PathFor(relativePath));

    public static void AssertOrdered(string text, string first, string second)
    {
        int firstIndex = text.IndexOf(first, StringComparison.Ordinal);
        int secondIndex = text.IndexOf(second, firstIndex < 0 ? 0 : firstIndex, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, $"marker not found: {first}");
        Assert.True(secondIndex > firstIndex, $"marker '{second}' did not follow '{first}'");
    }

    private static string FindRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "WindowsCareKit.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate WindowsCareKit.slnx from the test output directory.");
    }
}

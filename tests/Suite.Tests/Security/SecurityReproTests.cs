using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Controls;
using WindowsCareKit.App.Execution;
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
using WindowsCareKit.Module.Backup.ViewModels;
using WindowsCareKit.Module.Uninstall;
using WindowsCareKit.Module.Uninstall.ViewModels;
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

    /// <summary>C1 execution boundary: the destructive AppX COM sink is confined to Suite.Execution.</summary>
    [Fact]
    public void C1_appx_remove_sink_exists_only_in_the_sanctioned_execution_layer()
    {
        string[] sinks = Directory
            .EnumerateFiles(RepoSource.PathFor("src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(".RemovePackageAsync(", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepoSource.PathFor("."), path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["src/Suite.Execution/Adapters/AppxRemoveAdapter.cs"], sinks);

        string banned = RepoSource.Read("BannedSymbols.txt");
        Assert.Contains(
            "M:Windows.Management.Deployment.PackageManager.RemovePackageAsync(System.String,Windows.Management.Deployment.RemovalOptions)",
            banned,
            StringComparison.Ordinal);
        Assert.Contains(
            "M:Windows.Management.Deployment.PackageManager.RemovePackageAsync(System.String)",
            banned,
            StringComparison.Ordinal);
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

    /// <summary>S5: the delete adapter walks all parent components before the destructive boundary.</summary>
    [Fact]
    public void S5_delete_adapter_walks_ancestor_components_for_reparse_before_deleting()
    {
        string executor = RepoSource.Read("src/Suite.Execution/GatedExecutor.cs");
        string adapter = RepoSource.Read("src/Suite.Execution/Adapters/RecycleBinFileDeleteAdapter.cs");

        Assert.Contains("_gate.Evaluate(action)", executor, StringComparison.Ordinal);
        Assert.Contains("_fileAdapter.Delete(file)", executor, StringComparison.Ordinal);
        Assert.Contains("File.GetAttributes(path)", adapter, StringComparison.Ordinal);
        Assert.Contains("GetDirectoryName", adapter, StringComparison.Ordinal);
        Assert.Contains("GuardNoReparseInAncestry", adapter, StringComparison.Ordinal);
    }

    /// <summary>S8: a non-elevated plan is blocked before machine-wide service dispatch.</summary>
    [Fact]
    public void S8_machine_wide_service_action_is_unavailable_without_elevation()
    {
        string manifest = RepoSource.Read("src/Suite.App.Wpf/app.manifest");
        Assert.Contains("requestedExecutionLevel level=\"asInvoker\"", manifest, StringComparison.Ordinal);

        var action = new ServiceDeleteAction
        {
            ServiceName = "ContosoUpdater",
            Operation = ServiceOperation.Delete,
            Description = "delete synthetic service",
            Reason = "security characterization",
        };
        // Non-elevated: the gate classifies this as unavailable BEFORE approval.
        SafetyGate notElevated = TestData.Gate(elevated: false);
        SafetyVerdict verdict = notElevated.Evaluate(action);
        Assert.False(verdict.Allowed);
        Assert.Contains("elevated", verdict.Reason, StringComparison.OrdinalIgnoreCase);

        // The plan therefore does not authorize; nothing is dispatched.
        using var fixture = new ExecutorFixture(gate: notElevated);
        var plan = new OperationPlan("synthetic", "security-repro", [action], T0);
        ExecutionReport report = fixture.Executor.ExecuteWithReport(plan, plan.ComputeHash());
        Assert.False(report.Authorized);
        Assert.Empty(fixture.Adapters.Dispatched);

        // Elevated: unchanged (allowed).
        Assert.True(TestData.Gate(elevated: true).Evaluate(action).Allowed);
    }

    /// <summary>S9: registry backup ACLs are applied at creation and failures are not swallowed.</summary>
    [Fact]
    public void S9_registry_backup_acl_is_applied_atomically_and_fails_closed()
    {
        string writer = RepoSource.Read("src/Suite.Execution/Adapters/RegFileBackupWriter.cs");
        Assert.DoesNotContain("Best-effort hardening", writer, StringComparison.Ordinal);
        Assert.Contains("FileSystemAclExtensions.Create", writer, StringComparison.Ordinal); // ACL applied at creation
        Assert.Contains("BuildRestrictiveSecurity", writer, StringComparison.Ordinal);
        // No swallow of ACL failures: the only catch is the CreateNew-collision remap, which rethrows.
        Assert.DoesNotContain("catch (Exception)", writer, StringComparison.Ordinal);

        string registryAdapter = RepoSource.Read("src/Suite.Execution/Adapters/RegistryDeleteAdapter.cs");
        RepoSource.AssertOrdered(registryAdapter, "_backupWriter.WriteBackup", "RegistryKey.OpenBaseKey");
    }

    /// <summary>S10: restore state staging is random, CreateNew, and reparse-checked.</summary>
    [Fact]
    public void S10_restore_state_staging_is_random_createnew_and_reparse_checked()
    {
        var store = new RestoreStateStore(new SanctionedFileWriter());
        string stateDir = @"C:\Users\alice\restore-state";
        string target = store.PathFor(stateDir);
        string source = RepoSource.Read("src/Suite.Execution/Adapters/SanctionedFileWriter.cs");

        Assert.Equal(RestoreStateStore.FileName, Path.GetFileName(target));
        Assert.DoesNotContain("string staging = path + \".wcktmp\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllText(staging, json)", source, StringComparison.Ordinal);
        Assert.Contains("RandomNumberGenerator.GetBytes", source, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("ReparsePoint", source, StringComparison.Ordinal);
    }

    /// <summary>S11: service image path is hash-bound and rechecked before mutation.</summary>
    [Fact]
    public void S11_service_image_path_is_bound_to_the_hash_and_requeried_at_execution()
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

        Assert.NotEqual(before.ComputeHash(), after.ComputeHash());

        string adapter = RepoSource.Read("src/Suite.Execution/Adapters/ServiceControlAdapter.cs");
        Assert.Contains("QueryServiceConfig", adapter, StringComparison.Ordinal);
        Assert.Contains("VerifyImagePathUnchanged", adapter, StringComparison.Ordinal);
        Assert.Contains("configuration changed since planning", adapter, StringComparison.Ordinal);
    }

    /// <summary>S-m1: undefined service/task enum values are rejected before dispatch.</summary>
    [Fact]
    public void S_m1_undefined_service_and_task_operations_are_blocked_by_the_gate()
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

        Assert.False(fixture.Gate.Evaluate(service).Allowed);
        Assert.False(fixture.Gate.Evaluate(task).Allowed);

        var plan = new OperationPlan("synthetic", "security-repro", [service, task], T0);
        ExecutionReport report = fixture.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.False(report.Authorized);
        Assert.Empty(fixture.Adapters.Dispatched);

        // Adapters no longer silently no-op an undefined operation.
        string svc = RepoSource.Read("src/Suite.Execution/Adapters/ServiceControlAdapter.cs");
        string tsk = RepoSource.Read("src/Suite.Execution/Adapters/ScheduledTaskAdapter.cs");
        Assert.Contains("Unsupported service operation", svc, StringComparison.Ordinal);
        Assert.Contains("Unsupported task operation", tsk, StringComparison.Ordinal);
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
        string copyAdapter = RepoSource.Read("src/Suite.Execution/Adapters/CopyAdapter.cs");
        Assert.Contains("ScanSourceStreamFully", copyAdapter, StringComparison.Ordinal);
        Assert.Contains("SecretScanHardCapBytes", copyAdapter, StringComparison.Ordinal);
    }

    /// <summary>S1(b): an unquoted synthetic key/value is detected.</summary>
    [Fact]
    public void S1_unquoted_synthetic_secret_assignment_is_detected()
    {
        byte[] content = Encoding.UTF8.GetBytes("api_key = synthetic_value");

        EmbeddedSecretScanResult result = EmbeddedSecretScanner.Scan(content, "settings.env");

        Assert.True(result.ContainsSecret);
    }

    /// <summary>S1(a): scan and copy share one stable source handle.</summary>
    [Fact]
    public void S1_copy_scans_and_copies_from_one_stable_handle()
    {
        string copy = RepoSource.Read("src/Suite.Execution/Adapters/CopyAdapter.cs");
        Assert.Contains("FileShare.Read,", copy, StringComparison.Ordinal);           // swap-proof share (no ReadWrite|Delete)
        Assert.DoesNotContain("FileShare.ReadWrite | FileShare.Delete", copy, StringComparison.Ordinal);
        Assert.Contains("VerifyStableSourceHandle", copy, StringComparison.Ordinal);  // handle-verified identity
        Assert.Contains("PublishFromHandle", copy, StringComparison.Ordinal);         // atomic publish, no path reopen
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

    /// <summary>S6: traversal targets are rejected before planning.</summary>
    [Fact]
    public void S6_backup_target_traversal_is_rejected_by_the_planner()
    {
        string payload = @"C:\Users\alice\wck-repro\payload";
        BackupEntry entry = CopyEntry(@"C:\Users\alice\source", @"..\victim.txt");

        BackupPlanResult result = Planner().BuildPlan(new BackupManifest([entry]), payload, T0);
        Assert.Empty(result.Plan.Actions);
        Assert.Contains(result.Skipped, s => s.Reason.Contains("escapes the backup payload root", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>S6: destinations inside their source are rejected before planning.</summary>
    [Fact]
    public void S6_destination_inside_source_is_rejected_by_the_planner()
    {
        string source = @"C:\Users\alice\wck-source";
        string payload = Path.Combine(source, "payload");

        BackupPlanResult result = Planner().BuildPlan(new BackupManifest([CopyEntry(source, "nested")]), payload, T0);
        Assert.Empty(result.Plan.Actions);
        Assert.Contains(result.Skipped, s => s.Reason.Contains("inside the source", StringComparison.OrdinalIgnoreCase));
    }

    private static BackupPlanner Planner() =>
        new(TestData.Gate(), new FakeEnvironmentExpander(), TestData.PayloadRoots());

    private static BackupEntry CopyEntry(string source, string target) => new(
        "security-repro", true, BackupMethod.Copy, "synthetic", source, target,
        Array.Empty<string>(), SecretHandling.Normal, 1, "merge-after-install", "synthetic", null);
}

/// <summary>Deterministic UI/reliability characterizations that never open an application window.</summary>
[Collection(WpfResourceCollection.Name)]
public sealed class UiReliabilitySecurityReproTests
{
    /// <summary>G2: a superseded uninstall load is discarded so the later refresh wins without duplicates.</summary>
    [Fact]
    public async Task G2_overlapping_uninstall_loads_discard_the_superseded_result()
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

        Assert.Equal(1, vm.AllRows.Count(r => r.DisplayName == "Duplicated App"));
    }

    /// <summary>G3: AsyncRelayCommand observes a post-await fault (routes it to onError, never to the dispatcher)
    /// and guards re-entrancy so a second invocation while running is ignored.</summary>
    [Fact]
    public async Task G3_async_relay_command_observes_faults_and_guards_reentrancy()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new ExceptionCapturingSynchronizationContext();
        SynchronizationContext? prior = SynchronizationContext.Current;
        int invocations = 0;

        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var command = new AsyncRelayCommand(
                async _ =>
                {
                    Interlocked.Increment(ref invocations);
                    entered.SetResult();
                    await release.Task;
                    throw new InvalidOperationException("synthetic post-await failure");
                },
                onError: ex => faulted.TrySetResult(ex));

            command.Execute(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Re-entrancy guard: while the first run is in flight, CanExecute is false and a second Execute is ignored.
            Assert.False(command.CanExecute(null));
            command.Execute(null);
            Assert.Equal(1, Volatile.Read(ref invocations));

            // The fault has not surfaced yet, and nothing has escaped to the dispatcher.
            Assert.False(faulted.Task.IsCompleted);
            Assert.False(context.Exception.Task.IsCompleted);

            release.SetResult();
            Exception observed = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("synthetic post-await failure", observed.Message);

            // The fault was ROUTED to onError, NOT posted to the SynchronizationContext as an unhandled exception.
            Assert.False(context.Exception.Task.IsCompleted);
            Assert.Equal(typeof(void), typeof(AsyncRelayCommand).GetMethod(nameof(AsyncRelayCommand.Execute))!.ReturnType);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prior);
        }
    }

    /// <summary>
    /// G3 integration guard: constructing a plain RelayCommand from an async lambda creates an async-void
    /// boundary outside AsyncRelayCommand, so post-await faults escape to WPF's dispatcher. Every asynchronous
    /// view-model command must use the fault-observing command type, not merely keep that type available.
    /// </summary>
    [Fact]
    public void G3_async_view_model_commands_use_the_fault_observing_command_type()
    {
        string[] offenders = Directory
            .EnumerateFiles(RepoSource.PathFor("src"), "*ViewModel.cs", SearchOption.AllDirectories)
            .Where(path => Regex.IsMatch(
                File.ReadAllText(path),
                @"new\s+RelayCommand\s*\(\s*async\b",
                RegexOptions.CultureInvariant))
            .Select(path => Path.GetRelativePath(RepoSource.PathFor("."), path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);

        // The Uninstall gate's approve callback must stay the awaited Func<Task> overload. Handing it a
        // fire-and-forget lambda would drop a post-await fault on the destructive path — the same defect the
        // regex above forbids, at the one seam that actually runs an uninstaller.
        string uninstallViewModel = RepoSource.Read(
            "src/Suite.Module.Uninstall/ViewModels/UninstallViewModel.cs");
        Assert.DoesNotMatch(
            @"onApprove:\s*\(\)\s*=>\s*_\s*=\s*ApproveAsync\s*\(\s*\)",
            uninstallViewModel);
    }

    /// <summary>G4/NEW-04: PayloadDir cannot change while backup planning is in flight.</summary>
    [Fact]
    public async Task G4_backup_build_refuses_payload_change_while_planning_is_in_flight()
    {
        string source = @"C:\Users\alice\AppData\Roaming\Contoso";
        string payloadA = @"C:\Users\alice\backup-a";
        string payloadB = @"C:\Users\alice\backup-b";
        var loader = new BlockingManifestLoader(CopyEntry(source, "contoso"));
        var vm = new BackupViewModel(
            TestI18n.Full("en"),
            loader,
            new BackupPlanner(
                TestData.Gate(),
                new FakeEnvironmentExpander(),
                TestData.PayloadRoots()),
            null!)
        {
            PayloadDir = payloadA,
        };

        Task build = vm.BuildPlanAsync();
        Assert.True(loader.Entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(vm.CanEditDirectories);
        vm.PayloadDir = payloadB;
        Assert.Equal(payloadA, vm.PayloadDir);
        Assert.False(vm.HasPlan);

        loader.Release.Set();
        await build.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(payloadA, vm.PayloadDir);
        Assert.True(vm.HasPlan);
        Assert.Single(vm.PlanRows);
    }

    /// <summary>G-m3: inventory failures are typed and surfaced instead of rendering as a legitimate empty inventory.</summary>
    [Fact]
    public async Task G_m3_probe_failure_reports_unavailable_and_surfaces_an_inventory_notice()
    {
        var probe = new ThrowingRegistryProbe();
        var installed = new Win32InstalledAppReader(probe);
        InstalledAppReadResult outcome = installed.ReadAllWithStatus();

        Assert.Equal(InstalledAppReadStatus.Unavailable, outcome.Status);
        Assert.Empty(outcome.Apps);
        Assert.Equal(3, outcome.FailedSources.Count);
        Assert.Equal(3, probe.CallCount);

        UninstallViewModel vm = BuildUninstallVm(installed, new EmptyAppxReader());

        await vm.LoadAsync();

        Assert.Equal(6, probe.CallCount);
        Assert.Empty(vm.AllRows);
        Assert.False(vm.IsLoading);
        Assert.True(vm.HasInventoryNotice);
        Assert.False(string.IsNullOrWhiteSpace(vm.InventoryNotice));

        InstalledAppReadResult partial = new Win32InstalledAppReader(
            new PartiallyThrowingRegistryProbe()).ReadAllWithStatus();
        Assert.Equal(InstalledAppReadStatus.Partial, partial.Status);
        Assert.Empty(partial.Apps);
        Assert.Equal([InstalledAppSource.MachineWide64], partial.FailedSources);

        IInstalledAppReader completeReader = new Win32InstalledAppReader(new EmptyRegistryProbe());
        InstalledAppReadResult complete = completeReader.ReadAllWithStatus();
        Assert.Equal(InstalledAppReadStatus.Complete, complete.Status);
        Assert.Empty(complete.FailedSources);

        UninstallViewModel emptyVm = BuildUninstallVm(completeReader, new EmptyAppxReader());
        await emptyVm.LoadAsync();

        Assert.Empty(emptyVm.AllRows);
        Assert.False(emptyVm.HasInventoryNotice);
        Assert.Equal(string.Empty, emptyVm.InventoryNotice);
    }

    /// <summary>G-m3 AppX: a failed package inventory is typed and cannot masquerade as a valid empty list.</summary>
    [Fact]
    public void G_m3_appx_inventory_has_a_typed_health_result_consumed_by_the_ui()
    {
        string contract = RepoSource.Read("src/Suite.Core/Modules/Uninstall/Readers.cs");
        string reader = RepoSource.Read("src/Suite.Win32/Win32AppxReader.cs");
        string viewModel = RepoSource.Read("src/Suite.Module.Uninstall/ViewModels/UninstallViewModel.cs");

        Assert.Contains("AppxReadResult ReadCurrentUserPackagesWithStatus()", contract, StringComparison.Ordinal);
        Assert.Contains("public AppxReadResult ReadCurrentUserPackagesWithStatus()", reader, StringComparison.Ordinal);
        Assert.Contains("_appxReader.ReadCurrentUserPackagesWithStatus()", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task G_m3_unavailable_appx_inventory_surfaces_a_partial_inventory_notice()
    {
        var vm = BuildUninstallVm(
            new Win32InstalledAppReader(new EmptyRegistryProbe()),
            new UnavailableAppxReader());

        await vm.LoadAsync();

        Assert.Empty(vm.AllRows);
        Assert.True(vm.HasInventoryNotice);
        Assert.False(string.IsNullOrWhiteSpace(vm.InventoryNotice));
    }

    /// <summary>G-m4: the render harness rejects PathError diagnostics and nullable strings avoid Length paths.</summary>
    [Fact]
    public void G_m4_render_smoke_harness_rejects_broken_bindings_and_nullable_strings_are_path_safe()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Exception diagnostic = Assert.ThrowsAny<Exception>(() =>
                    ViewRenderSmokeTests.AssertNoBindingWarnings(() =>
                    {
                        var text = new TextBlock { DataContext = new BrokenBindingSource() };
                        var binding = new Binding("DefinitelyMissing.Value");
                        PresentationTraceSources.SetTraceLevel(binding, PresentationTraceLevel.High);
                        text.SetBinding(TextBlock.TextProperty, binding);
                        text.Measure(new Size(200, 40));
                        text.Arrange(new Rect(0, 0, 200, 40));
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                        BindingExpression expression = Assert.IsType<BindingExpression>(
                            BindingOperations.GetBindingExpression(text, TextBlock.TextProperty));
                        Assert.Equal(BindingStatus.PathError, expression.Status);
                        ViewRenderSmokeTests.AssertNoBindingErrors(text);
                    }));
                Assert.Contains("DefinitelyMissing", diagnostic.Message, StringComparison.Ordinal);
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

        var converter = new NonEmptyToVisibleConverter();
        Assert.Equal(Visibility.Collapsed, converter.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, converter.Convert(string.Empty, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Visible, converter.Convert("detail", typeof(Visibility), null, CultureInfo.InvariantCulture));

        foreach (string view in Directory.EnumerateFiles(RepoSource.PathFor("src"), "*.xaml", SearchOption.AllDirectories))
        {
            string xaml = File.ReadAllText(view);
            Assert.DoesNotContain(".Length, Converter={StaticResource PositiveToVis}", xaml, StringComparison.Ordinal);
        }
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

        public BackupManifestLoadResult LoadFromDirectory(string manifestsDirectory)
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("synthetic manifest load was not released");
            return BackupManifestLoadResult.Complete(new BackupManifest([entry]));
        }

        public BackupManifestLoadResult LoadFromJson(IEnumerable<string> jsonDocuments)
            => BackupManifestLoadResult.Complete(new BackupManifest([entry]));
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

    private sealed class PartiallyThrowingRegistryProbe : IRegistryProbe
    {
        public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, RegistryView view, string subKey)
            => hive == RegistryHive.LocalMachine && view == RegistryView.Registry64
                ? throw new UnauthorizedAccessException("synthetic probe failure")
                : Array.Empty<string>();

        public RegistryKeySnapshot? ReadKey(RegistryHive hive, RegistryView view, string subKey)
            => throw new InvalidOperationException("entry reads must not occur for an empty synthetic source");
    }

    private sealed class EmptyRegistryProbe : IRegistryProbe
    {
        public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, RegistryView view, string subKey)
            => Array.Empty<string>();

        public RegistryKeySnapshot? ReadKey(RegistryHive hive, RegistryView view, string subKey)
            => throw new InvalidOperationException("entry reads must not occur for an empty synthetic source");
    }

    private sealed class EmptyAppxReader : IAppxReader
    {
        public IReadOnlyList<InstalledAppx> ReadCurrentUserPackages() => Array.Empty<InstalledAppx>();
    }

    private sealed class UnavailableAppxReader : IAppxReader
    {
        public IReadOnlyList<InstalledAppx> ReadCurrentUserPackages() => Array.Empty<InstalledAppx>();

        public AppxReadResult ReadCurrentUserPackagesWithStatus()
            => new(Array.Empty<InstalledAppx>(), AppxReadStatus.Unavailable);
    }

    private sealed class NoOpExecutor : IPlanExecutor
    {
        public PlanExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash)
            => new(true, approvedPlanHash,
                plan.Actions.Select(a => new PlanActionResult(a.Id, a.Kind, PlanActionStatus.Done, "not used")).ToArray());
    }

    private sealed class NoOpFolderOpener : IFolderOpener
    {
        public void OpenFolder(string path) { }
    }

    private sealed class BrokenBindingSource;
}

/// <summary>Host-safe closure proofs for the findings carried into the 2026-07-11 continuation pass.</summary>
[Collection(WpfResourceCollection.Name)]
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

    /// <summary>S2: rejected directories never enter the lifetime-global dependency resolver.</summary>
    [Fact]
    public void S2_rejected_module_directory_is_not_a_global_dependency_probe()
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
        ModuleCatalogResult rejected = rejectedCatalog.LoadModules();
        Assert.Equal(new[] { "settings" }, rejected.Modules.Select(m => m.Id).ToArray());
        Assert.Contains(
            rejected.Health.Components,
            component => component.Status == ModuleComponentStatus.Malformed
                && component.FailureCategory == ModuleCatalogHealth.CategoryIdMismatch);

        string validRoot = workspace.Combine("valid-root");
        string validDirectory = Path.Combine(validRoot, "uninstall");
        Directory.CreateDirectory(validDirectory);
        File.Copy(knownModule.Location, Path.Combine(validDirectory, "Suite.Module.uninstall.dll"));
        var validCatalog = new DirectoryModuleCatalog(validRoot);
        Assert.Contains(validCatalog.LoadModules().Modules, module => module.Id == "uninstall");

        Type resolver = typeof(DirectoryModuleCatalog).Assembly.GetType(
            "WindowsCareKit.App.Modules.ModuleAssemblyResolver",
            throwOnError: true)!;
        MethodInfo handler = resolver.GetMethod("OnResolving", BindingFlags.Static | BindingFlags.NonPublic)!;
        string? resolvedLocation = ResolveInCollectibleContext(handler, syntheticDependencyName);

        // FIXED: the rejected directory was never registered, so its synthetic dependency is not resolvable.
        Assert.Null(resolvedLocation);
        Assert.False(File.Exists(Path.Combine(validDirectory, syntheticDependencyName + ".dll")));
    }

    /// <summary>G1: literal Turkish keyboard input uses the selected UI culture even when process culture is en-US.</summary>
    [Fact]
    public void G1_turkish_confirmation_uses_selected_ui_culture()
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
            Assert.True(gate.TypedMatches);
            Assert.True(gate.CanApprove);
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

    /// <summary>G6: production-style bindings follow a language switch while chip color follows the theme.</summary>
    [Fact]
    public void G6_plan_row_display_follows_language_and_theme_switch()
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
                PlanRow row = PlanRow.FromAction(action, isWholeTree: true, i18n);

                var host = new StackPanel();
                host.Resources.MergedDictionaries.Add(LoadTheme("Strongbox"));
                var actionText = BoundText(row, nameof(PlanRow.Text));
                var riskChip = new RiskChip { DataContext = row };
                riskChip.SetBinding(RiskChip.RiskProperty, new Binding(nameof(PlanRow.Risk)));
                riskChip.SetBinding(RiskChip.IsBlockedProperty, new Binding(nameof(PlanRow.IsSkipped)));
                riskChip.SetBinding(RiskChip.TextProperty, new Binding(nameof(PlanRow.RiskText)));
                var undoText = BoundText(row, nameof(PlanRow.Undo));
                host.Children.Add(actionText);
                host.Children.Add(riskChip);
                host.Children.Add(undoText);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

                FamilyChip familyChip = Assert.IsType<FamilyChip>(riskChip.Content);
                Border chipRoot = Assert.IsType<Border>(familyChip.Content);

                string beforeActionText = actionText.Text;
                string beforeRiskText = riskChip.Text;
                string beforeUndoText = undoText.Text;
                Color beforeDisplayedColor = Assert.IsType<SolidColorBrush>(chipRoot.Background).Color;

                i18n.SelectedCulture = "tr";
                host.Resources.MergedDictionaries[0] = LoadTheme("Daylight");
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                Color daylightAttention = Assert.IsType<SolidColorBrush>(host.FindResource("Wck.Attention.Wash")).Color;

                Assert.Equal("tr", i18n.SelectedCulture);
                Assert.NotEqual(beforeActionText, actionText.Text);
                Assert.StartsWith("Kopyala: ", actionText.Text, StringComparison.Ordinal);
                Assert.Contains("(tüm klasör kopyası)", row.Detail, StringComparison.Ordinal);
                Assert.NotEqual(beforeRiskText, riskChip.Text);
                Assert.Equal("Orta", riskChip.Text);
                Assert.NotEqual(beforeUndoText, undoText.Text);
                Assert.Equal("geri al: Kısmi", undoText.Text);
                Assert.NotEqual(beforeDisplayedColor, Assert.IsType<SolidColorBrush>(chipRoot.Background).Color);
                Assert.Equal(daylightAttention, Assert.IsType<SolidColorBrush>(chipRoot.Background).Color);
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

    /// <summary>G-m2: preview and result branches use phase-specific, unambiguous localization keys.</summary>
    [Fact]
    public void G_m2_preview_and_result_summaries_use_distinct_truthful_keys()
    {
        string backup = RepoSource.Read("src/Suite.Module.Backup/ViewModels/BackupViewModel.cs");
        string migration = RepoSource.Read("src/Suite.Module.Migration/ViewModels/MigrationViewModel.cs");
        string backupEnglish = RepoSource.Read("src/Suite.Module.Backup/lang/en.json");
        string migrationEnglish = RepoSource.Read("src/Suite.Module.Migration/lang/en.json");

        Assert.Equal(1, CountOccurrences(backup, "I18n.Format(\"backup.report.summaryShortPreview\""));
        Assert.Equal(1, CountOccurrences(backup, "I18n.Format(\"backup.report.summaryShortResult\""));
        Assert.Equal(1, CountOccurrences(migration, "\"migration.capture.previewSummary\""));
        Assert.Equal(1, CountOccurrences(migration, "\"migration.capture.resultSummary\""));
        Assert.Contains("{0} to copy", backupEnglish, StringComparison.Ordinal);
        Assert.Contains("{0} copied", backupEnglish, StringComparison.Ordinal);
        Assert.Contains("{0} to copy", migrationEnglish, StringComparison.Ordinal);
        Assert.Contains("{0} copied", migrationEnglish, StringComparison.Ordinal);
        Assert.DoesNotContain("copied or planned", migrationEnglish, StringComparison.Ordinal);
    }

    /// <summary>G-m5: URL and folder launchers dispose the local process handle after a fire-and-forget launch.</summary>
    [Fact]
    public void G_m5_url_and_folder_openers_dispose_returned_process_handles()
    {
        Assert.Equal(typeof(void), typeof(IUrlOpener).GetMethod(nameof(IUrlOpener.Open))!.ReturnType);
        Assert.Equal(typeof(void), typeof(IFolderOpener).GetMethod(nameof(IFolderOpener.OpenFolder))!.ReturnType);

        var urlProcess = new DisposalTrackingProcess();
        var urlOpener = new UrlOpener(_ => urlProcess);
        urlOpener.Open(new Uri("https://example.invalid/synthetic"));
        Assert.True(urlProcess.WasDisposed);

        string directory = Path.Combine(Path.GetTempPath(), "wck-gm5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string fullPath = Path.GetFullPath(directory);
            var folderProcess = new DisposalTrackingProcess();
            var canonicalizer = new FakeCanonicalizer().Map(
                fullPath, fullPath, reparse: false, resolved: true);
            var folderOpener = new FolderOpener(canonicalizer, _ => folderProcess);

            folderOpener.OpenFolder(directory);

            Assert.True(folderProcess.WasDisposed);
        }
        finally
        {
            TestFs.DeleteResilient(directory);
        }
    }

    /// <summary>G-m6: uninstall search follows the active culture for dotted and dotless Turkish I.</summary>
    [Fact]
    public async Task G_m6_uninstall_search_matches_turkish_dotted_and_dotless_i()
    {
        CultureInfo priorCulture = CultureInfo.CurrentCulture;
        CultureInfo priorUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo turkish = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentCulture = turkish;
            CultureInfo.CurrentUICulture = turkish;
            var reader = new StaticInstalledAppReader([
                TestData.App(displayName: "\u0130stanbul Araçları", source: InstalledAppSource.CurrentUser),
                TestData.App(displayName: "\u0131rmak Yardımcısı", source: InstalledAppSource.CurrentUser),
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
            vm.Search = "istanbul";

            Assert.Equal("tr", vm.I18n.SelectedCulture);
            Assert.Equal("\u0130stanbul Araçları", Assert.Single(vm.AppsView.Cast<AppRow>()).DisplayName);

            vm.Search = "IRMAK";
            Assert.Equal("\u0131rmak Yardımcısı", Assert.Single(vm.AppsView.Cast<AppRow>()).DisplayName);

            CultureInfo english = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentCulture = english;
            CultureInfo.CurrentUICulture = english;
            var englishVm = new UninstallViewModel(
                ShellI18n("en"),
                new StaticInstalledAppReader([
                    TestData.App(displayName: "Synthetic Utility", source: InstalledAppSource.CurrentUser),
                ]),
                new EmptyAppxReader(),
                TestData.Gate(),
                new FakeLeftoverProbe(),
                new NoOpExecutor(),
                new NoOpFolderOpener());

            await englishVm.LoadAsync();
            englishVm.Search = "UTILITY";

            Assert.Equal("Synthetic Utility", Assert.Single(englishVm.AppsView.Cast<AppRow>()).DisplayName);
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
            if (resolved is not null)
                Assert.Equal(dependencyName, resolved.GetName().Name);
            return resolved?.Location;
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
            Service = new MigrationRestoreService(runner, executor, new RestoreStateStore(new SanctionedFileWriter()));
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

    private sealed class DisposalTrackingProcess : Process
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class EmptyAppxReader : IAppxReader
    {
        public IReadOnlyList<InstalledAppx> ReadCurrentUserPackages() => Array.Empty<InstalledAppx>();
    }

    private sealed class NoOpExecutor : IPlanExecutor
    {
        public PlanExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash)
            => new(true, approvedPlanHash,
                plan.Actions.Select(a => new PlanActionResult(a.Id, a.Kind, PlanActionStatus.Done, "not used")).ToArray());
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

    /// <summary>§6 release inputs: one validated tag version drives both the .NET binaries and installer metadata.</summary>
    [Fact]
    public void Release_tag_version_is_validated_and_applied_to_every_binary_output()
    {
        string release = RepoSource.Read(".github/workflows/release.yml");

        Assert.Contains("name: Resolve build version", release, StringComparison.Ordinal);
        Assert.Contains("BUILD_VERSION=$buildVersion", release, StringComparison.Ordinal);
        Assert.Contains("BUILD_VERSION_NUM=$numericVersion", release, StringComparison.Ordinal);
        Assert.Contains("ARTIFACT_VERSION=$artifactVersion", release, StringComparison.Ordinal);
        Assert.Contains("^v(?<version>", release, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(
                release,
                @"-p:Version=\$\{\{ env\.BUILD_VERSION \}\}",
                RegexOptions.CultureInvariant).Count);
        Assert.Contains("$appVer = \"${{ env.BUILD_VERSION }}\"", release, StringComparison.Ordinal);
        Assert.Contains("$appVerNum = \"${{ env.BUILD_VERSION_NUM }}\"", release, StringComparison.Ordinal);
        Assert.DoesNotContain("$appVer = $refName.TrimStart('v')", release, StringComparison.Ordinal);
        RepoSource.AssertOrdered(release, "name: Resolve build version", "name: Build");
    }

    /// <summary>G5: the release step throws on a failed upload/edit/create before the job can report success.</summary>
    [Fact]
    public void G5_release_asset_commands_are_exit_code_checked()
    {
        string release = RepoSource.Read(".github/workflows/release.yml");

        // The upload's exit code is checked and rethrown BEFORE the edit runs, so a failed upload cannot be
        // masked by a later successful edit.
        RepoSource.AssertOrdered(release, "gh release upload", "if ($LASTEXITCODE -ne 0)");
        RepoSource.AssertOrdered(release, "throw \"gh release upload failed", "gh release edit");

        Assert.Contains("throw \"gh release upload failed", release, StringComparison.Ordinal);
        Assert.Contains("throw \"gh release edit failed", release, StringComparison.Ordinal);
        Assert.Contains("throw \"gh release create failed", release, StringComparison.Ordinal);

        // The blanket native-error preference must NOT be used here (it would throw on the intentional
        // `gh release view` existence probe and break the create path).
        Assert.DoesNotContain("PSNativeCommandUseErrorActionPreference", release, StringComparison.Ordinal);
    }

    /// <summary>
    /// The artifact gate's report is ASSERTED, not merely captured. Capturing stdout and rejecting a nonzero
    /// exit still passes a regression that recognises <c>--verify-layout</c> and returns 0 without writing
    /// its line — a silent successful no-op, which for a gate is the worst failure mode there is.
    /// <para>
    /// A source guard, because the failure boundary is a GitHub Actions step this suite cannot host. The
    /// assertion's actual behaviour against a printing and a silent verifier was exercised by hand at the
    /// process boundary when it was written; this is what keeps it from being quietly removed afterwards.
    /// </para>
    /// </summary>
    [Fact]
    public void The_layout_verifier_step_requires_the_report_line_it_captures()
    {
        string release = RepoSource.Read(".github/workflows/release.yml");

        // Order matters: the exit code is the authoritative failure signal, so a nonzero exit must still
        // throw with its own message rather than being re-diagnosed as a missing report.
        RepoSource.AssertOrdered(
            release,
            "throw \"The released artifact fails its own layout check",
            "$okLines = @(@(Get-Content $verifyOut");

        Assert.Contains("""'^WCK-LAYOUT status=Ok(\s|$)'""", release, StringComparison.Ordinal);
        Assert.Contains("if ($okLines.Count -ne 1)", release, StringComparison.Ordinal);
    }

    /// <summary>§6 destination race: reparse validation and staged destination publish remain separate operations.</summary>
    [Fact]
    public void Destination_reparse_check_and_publish_are_not_handle_bound()
    {
        string copy = RepoSource.Read("src/Suite.Execution/Adapters/CopyAdapter.cs");
        RepoSource.AssertOrdered(copy, "GuardDestinationNotReparse(destination)", "PublishFromHandle(src, destination)");
        Assert.Contains("SafeFileHandle", copy, StringComparison.Ordinal);
    }
}

/// <summary>OSS-facing documentation guards for the project's non-destructive default workflow.</summary>
public sealed class OssDocumentationSecurityReproTests
{
    [Theory]
    [InlineData("README.md")]
    [InlineData("README.tr.md")]
    [InlineData("CONTRIBUTING.md")]
    [InlineData("AGENTS.md")]
    public void Public_host_test_commands_explicitly_exclude_the_destructive_tier(string relativePath)
    {
        string[] commands = RepoSource.Read(relativePath)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("dotnet test ", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(commands);
        Assert.All(
            commands,
            command => Assert.Contains("Category!=Destructive", command, StringComparison.Ordinal));
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

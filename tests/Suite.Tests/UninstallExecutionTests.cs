using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// The Sil execution wiring: staging a run only asks for confirmation (the executor is NOT called), and
/// only Approve runs the plan through <see cref="IExecutor"/> with the hash captured from the EXACT staged
/// plan. AppX removal goes through <see cref="IAppxRemover"/>, never the typed-action executor. No real OS
/// destruction happens — everything is faked (spec §1.1, §3).
/// </summary>
public class UninstallExecutionTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static UninstallViewModel BuildVm(
        IExecutor executor, FakeAppxRemover remover,
        FakeLeftoverProbe? probe = null, IReadOnlyList<InstalledApp>? apps = null, IReadOnlyList<InstalledAppx>? appx = null)
    {
        var i18n = new I18n();
        var appReader = new FakeInstalledAppReader(apps ?? new[] { TestData.App(installLocation: @"C:\Program Files\SomeApp") });
        var appxReader = new FakeAppxReader(appx ?? Array.Empty<InstalledAppx>());
        var gate = TestData.Gate();
        probe ??= new FakeLeftoverProbe();
        return new UninstallViewModel(i18n, appReader, appxReader, gate, probe, executor, remover);
    }

    private static async Task PumpAsync(Func<bool> until, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10);
        Assert.True(until(), "operation did not complete in time");
    }

    [Fact]
    public async Task Staging_leftovers_asks_to_confirm_and_does_not_execute()
    {
        var executor = new FakeExecutor();
        var vm = BuildVm(executor, new FakeAppxRemover(),
            probe: ProbeWithSafeLeftover(),
            apps: new[] { TestData.App(installLocation: @"C:\Program Files\SomeApp") });

        await vm.LoadAsync();
        vm.SelectedApp = vm.Apps[0];
        await PumpAsync(() => vm.LeftoverActions.Count > 0);

        vm.RunLeftoverCommand.Execute(null);

        Assert.True(vm.RequiresConfirmation);
        Assert.Equal(0, executor.CallCount); // NOTHING ran on staging
    }

    [Fact]
    public async Task Cancel_clears_the_confirmation_without_executing()
    {
        var executor = new FakeExecutor();
        var vm = BuildVm(executor, new FakeAppxRemover(),
            probe: ProbeWithSafeLeftover(),
            apps: new[] { TestData.App(installLocation: @"C:\Program Files\SomeApp") });

        await vm.LoadAsync();
        vm.SelectedApp = vm.Apps[0];
        await PumpAsync(() => vm.LeftoverActions.Count > 0);

        vm.RunLeftoverCommand.Execute(null);
        vm.CancelCommand.Execute(null);

        Assert.False(vm.RequiresConfirmation);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task Approve_executes_the_exact_staged_plan_hash()
    {
        var executor = new FakeExecutor();
        var vm = BuildVm(executor, new FakeAppxRemover(),
            probe: ProbeWithSafeLeftover(),
            apps: new[] { TestData.App(installLocation: @"C:\Program Files\SomeApp") });

        await vm.LoadAsync();
        vm.SelectedApp = vm.Apps[0];
        await PumpAsync(() => vm.LeftoverActions.Count > 0);

        vm.RunLeftoverCommand.Execute(null);
        vm.ApproveCommand.Execute(null);

        // Wait for the run to FULLY settle (result rendered), not just for the mid-run executor call —
        // HasResult is set on the continuation after the executor returns, so pump on it to avoid a race.
        await PumpAsync(() => vm.HasResult && !vm.RequiresConfirmation);

        Assert.Equal(1, executor.CallCount);
        Assert.NotNull(executor.LastPlan);
        // The hash the VM passed must equal the staged plan's own hash (no drift between preview and run).
        Assert.Equal(executor.LastPlan!.ComputeHash(), executor.LastHash);
        Assert.False(vm.RequiresConfirmation);
        Assert.True(vm.HasResult);
    }

    [Fact]
    public async Task Official_run_stages_a_command_action_plan()
    {
        var executor = new FakeExecutor();
        var app = TestData.App(uninstall: "\"C:\\Program Files\\SomeApp\\uninst.exe\" /S");
        var vm = BuildVm(executor, new FakeAppxRemover(), apps: new[] { app });

        await vm.LoadAsync();
        vm.SelectedApp = vm.Apps[0];
        await PumpAsync(() => vm.OfficialActions.Count > 0);

        vm.RunOfficialCommand.Execute(null);
        vm.ApproveCommand.Execute(null);

        await PumpAsync(() => executor.CallCount == 1);

        Assert.NotNull(executor.LastPlan);
        Assert.Single(executor.LastPlan!.Actions);
        Assert.IsType<CommandAction>(executor.LastPlan!.Actions[0]);
    }

    [Fact]
    public async Task Real_gated_executor_dispatches_a_safe_leftover_to_the_file_adapter()
    {
        using var fx = new WindowsCareKit.Tests.Execution.ExecutorFixture(TestData.Gate());
        var vm = BuildVm(fx.Executor, new FakeAppxRemover(),
            probe: ProbeWithSafeLeftover(),
            apps: new[] { TestData.App(installLocation: @"C:\Program Files\SomeApp") });

        await vm.LoadAsync();
        vm.SelectedApp = vm.Apps[0];
        await PumpAsync(() => vm.LeftoverActions.Count > 0);

        vm.RunLeftoverCommand.Execute(null);
        vm.ApproveCommand.Execute(null);

        await PumpAsync(() => vm.HasResult && !vm.RequiresConfirmation);

        // The safe leftover folder routed to the (fake) file-delete adapter exactly once.
        Assert.Single(fx.Adapters.Calls);
        Assert.StartsWith("file:", fx.Adapters.Calls[0]);
    }

    [Fact]
    public async Task Appx_removal_goes_through_the_remover_not_the_executor()
    {
        var executor = new FakeExecutor();
        var remover = new FakeAppxRemover { Result = new AppxRemovalResult(true, "removed") };
        var package = new InstalledAppx { PackageFullName = "Contoso.App_1.0.0.0_x64__abc", DisplayName = "Contoso" };
        var vm = BuildVm(executor, remover, appx: new[] { package });

        await vm.LoadAsync();
        vm.SelectedAppx = vm.AppxApps[0];

        vm.RemoveAppxCommand.Execute(null);
        Assert.True(vm.RequiresConfirmation); // still gated behind confirm

        vm.ApproveCommand.Execute(null);
        await PumpAsync(() => vm.HasResult); // settle the full approve→remove→render before asserting

        Assert.Equal(0, executor.CallCount);              // never the typed-action executor
        Assert.Equal(1, remover.CallCount);
        Assert.True(vm.HasResult);
        Assert.Empty(vm.AppxApps);                          // removed from the list on success
    }

    [Fact]
    public async Task Appx_removal_failure_keeps_the_app_in_the_list()
    {
        var remover = new FakeAppxRemover { Result = new AppxRemovalResult(false, "refused") };
        var package = new InstalledAppx { PackageFullName = "Contoso.App_1.0.0.0_x64__abc", DisplayName = "Contoso" };
        var vm = BuildVm(new FakeExecutor(), remover, appx: new[] { package });

        await vm.LoadAsync();
        vm.SelectedAppx = vm.AppxApps[0];
        vm.RemoveAppxCommand.Execute(null);
        vm.ApproveCommand.Execute(null);
        await PumpAsync(() => vm.HasResult); // settle the full approve→remove→render before asserting

        Assert.Equal(1, remover.CallCount);
        Assert.True(vm.HasResult);
        Assert.Single(vm.AppxApps); // not removed
    }

    private static FakeLeftoverProbe ProbeWithSafeLeftover()
    {
        var probe = new FakeLeftoverProbe();
        probe.Directories.Add(new LeftoverDirectory(@"C:\Program Files\SomeApp", "leftover app folder"));
        return probe;
    }

    // ---- fakes ----

    private sealed class FakeInstalledAppReader(IReadOnlyList<InstalledApp> apps) : IInstalledAppReader
    {
        public IReadOnlyList<InstalledApp> ReadAll() => apps;
    }

    private sealed class FakeAppxReader(IReadOnlyList<InstalledAppx> packages) : IAppxReader
    {
        public IReadOnlyList<InstalledAppx> ReadCurrentUserPackages() => packages;
    }

    private sealed class FakeExecutor : IExecutor
    {
        public int CallCount { get; private set; }
        public OperationPlan? LastPlan { get; private set; }
        public string? LastHash { get; private set; }

        public ExecutionOutcome Execute(OperationPlan plan, string approvedPlanHash)
        {
            CallCount++;
            LastPlan = plan;
            LastHash = approvedPlanHash;
            return new ExecutionOutcome(true, "faked");
        }
    }

    private sealed class FakeAppxRemover : IAppxRemover
    {
        public AppxRemovalResult Result { get; set; } = new(true, "removed");
        public int CallCount { get; private set; }

        public Task<AppxRemovalResult> RemoveCurrentUserAsync(InstalledAppx package, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }
}

using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
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
        return new UninstallViewModel(i18n, appReader, appxReader, gate, probe, executor, remover, new FakeFolderOpener());
    }

    /// <summary>Selects the unified-grid row backed by the first desktop app (mirrors a user clicking it).</summary>
    private static void SelectFirstApp(UninstallViewModel vm)
        => vm.SelectedRow = vm.AllRows.First(r => r.App is not null);

    /// <summary>Selects the unified-grid row backed by the first Store app.</summary>
    private static void SelectFirstAppx(UninstallViewModel vm)
        => vm.SelectedRow = vm.AllRows.First(r => r.Appx is not null);

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
            probe: ProbeWithProgramOwnedLeftover(),
            apps: new[] { TestData.App(installLocation: @"C:\Program Files\SomeApp") });

        await vm.LoadAsync();
        SelectFirstApp(vm);
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
            probe: ProbeWithProgramOwnedLeftover(),
            apps: new[] { TestData.App(installLocation: @"C:\Program Files\SomeApp") });

        await vm.LoadAsync();
        SelectFirstApp(vm);
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
            probe: ProbeWithProgramOwnedLeftover(),
            apps: new[] { TestData.App(installLocation: @"C:\Program Files\SomeApp") });

        await vm.LoadAsync();
        SelectFirstApp(vm);
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
        SelectFirstApp(vm);
        await PumpAsync(() => vm.OfficialActions.Count > 0);

        vm.RunOfficialCommand.Execute(null);
        vm.ApproveCommand.Execute(null);

        await PumpAsync(() => executor.CallCount == 1);

        Assert.NotNull(executor.LastPlan);
        Assert.Single(executor.LastPlan!.Actions);
        Assert.IsType<CommandAction>(executor.LastPlan!.Actions[0]);
    }

    [Fact]
    public async Task Real_gated_executor_dispatches_a_program_owned_leftover_to_the_registry_adapter()
    {
        using var fx = new WindowsCareKit.Tests.Execution.ExecutorFixture(TestData.Gate());
        var vm = BuildVm(fx.Executor, new FakeAppxRemover(),
            probe: ProbeWithProgramOwnedLeftover(),
            apps: new[] { TestData.App(installLocation: @"C:\Program Files\SomeApp") });

        await vm.LoadAsync();
        SelectFirstApp(vm);
        await PumpAsync(() => vm.LeftoverActions.Count > 0);

        vm.RunLeftoverCommand.Execute(null);
        vm.ApproveCommand.Execute(null);

        await PumpAsync(() => vm.HasResult && !vm.RequiresConfirmation);

        // The ProgramOwned vendor-leaf leftover routed to the (fake) registry-delete adapter exactly once.
        Assert.Single(fx.Adapters.Calls);
        Assert.StartsWith("registry:", fx.Adapters.Calls[0]);
    }

    [Fact]
    public async Task Shared_and_protected_keys_never_reach_the_recording_registry_adapter()
    {
        // PR-3 A — the load-bearing integration test. A Shared vendor PARENT (HKLM\SOFTWARE\SomeVendor) and a
        // Protected key (HKLM\SOFTWARE\Microsoft\Windows) plus one genuine ProgramOwned leaf flow through the
        // REAL UninstallViewModel → stage → REAL GatedExecutor with a recording registry adapter. Assert that
        // ONLY the ProgramOwned leaf is staged + executed; the Shared/Protected targets are absent and the
        // adapter is never called for them.
        using var fx = new WindowsCareKit.Tests.Execution.ExecutorFixture(TestData.Gate());
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.MachineWide64);
        var vm = BuildVm(fx.Executor, new FakeAppxRemover(),
            probe: ProbeWithSharedProtectedAndOwned(), apps: new[] { app });

        await vm.LoadAsync();
        SelectFirstApp(vm);
        await PumpAsync(() => vm.LeftoverActions.Count > 0);

        // The previewed/staged plan contains ONLY the ProgramOwned vendor leaf — Shared/Protected excluded.
        Assert.Single(vm.LeftoverActions);

        vm.RunLeftoverCommand.Execute(null);
        vm.ApproveCommand.Execute(null);
        await PumpAsync(() => vm.HasResult && !vm.RequiresConfirmation);

        // The recording registry adapter was called EXACTLY ONCE — for the ProgramOwned leaf only.
        Assert.Single(fx.Adapters.Calls);
        Assert.StartsWith("registry:", fx.Adapters.Calls[0]);

        // Inspect the exact action the executor dispatched: it is the ProgramOwned vendor leaf, NOT the parent.
        var dispatched = Assert.IsType<RegistryDeleteAction>(Assert.Single(fx.Adapters.Dispatched));
        Assert.Equal(@"SOFTWARE\SomeVendor\SomeApp", dispatched.SubKeyPath);

        // The Shared vendor parent and the Protected system key were NEVER dispatched (call count 0 for each).
        Assert.DoesNotContain(fx.Adapters.Dispatched.OfType<RegistryDeleteAction>(),
            r => r.SubKeyPath.Equals(@"SOFTWARE\SomeVendor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fx.Adapters.Dispatched.OfType<RegistryDeleteAction>(),
            r => r.SubKeyPath.Equals(@"SOFTWARE\Microsoft\Windows", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Without_the_classifier_filter_a_shared_key_WOULD_reach_the_adapter()
    {
        // FAIL-WITHOUT proof (the non-vacuous counterpart of the test above). This bypasses the classifier the
        // way the OLD code did: it stages the RAW gate-allowed candidate plan (Shared + ProgramOwned) and runs
        // it through the REAL GatedExecutor. The Shared vendor parent DOES reach the recording adapter — proving
        // the classifier filter on the live path is exactly what keeps it out. Removing that filter regresses.
        using var fx = new WindowsCareKit.Tests.Execution.ExecutorFixture(TestData.Gate());
        var app = TestData.App(displayName: "SomeApp", publisher: "SomeVendor",
            source: InstalledAppSource.MachineWide64);

        // Build the RAW gate-allowed plan directly (no ProgramOwned filter) — what result.Plan used to be.
        var probe = ProbeWithSharedProtectedAndOwned();
        var scan = new LeftoverScanner(probe, TestData.Gate()).Scan(app, T0);
        var rawAllowed = scan.Candidates
            .Where(c => c.Classification != LeftoverClassification.Protected) // gate-allowed = Shared + ProgramOwned
            .Select(c => c.Action)
            .ToList();
        var rawPlan = new OperationPlan("raw", "uninstall", rawAllowed, T0);

        // Sanity: the raw plan carries BOTH the ProgramOwned leaf AND the Shared vendor parent.
        Assert.Equal(2, rawPlan.Actions.Count);

        ExecutionReport report = fx.Executor.ExecuteWithReport(rawPlan, rawPlan.ComputeHash());
        await Task.CompletedTask;

        Assert.True(report.Authorized); // the gate does NOT block Shared — only the classifier filter would
        // BOTH registry deletes reached the recording adapter (the regression the live filter prevents).
        Assert.Equal(2, fx.Adapters.Calls.Count);
        // The Shared vendor parent DID reach the adapter when the classifier filter is bypassed.
        Assert.Contains(fx.Adapters.Dispatched.OfType<RegistryDeleteAction>(),
            r => r.SubKeyPath.Equals(@"SOFTWARE\SomeVendor", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A probe emitting one ProgramOwned vendor leaf + one Shared vendor parent + one Protected system key.
    /// </summary>
    private static FakeLeftoverProbe ProbeWithSharedProtectedAndOwned()
    {
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "vendor leaf (ProgramOwned)"));
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\SomeVendor", RegistryView.Registry64, "vendor parent (Shared)"));
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\Microsoft\Windows", RegistryView.Registry64, "system key (Protected)"));
        return probe;
    }

    [Fact]
    public async Task Appx_removal_goes_through_the_remover_not_the_executor()
    {
        var executor = new FakeExecutor();
        var remover = new FakeAppxRemover { Result = new AppxRemovalResult(true, "removed") };
        var package = new InstalledAppx { PackageFullName = "Contoso.App_1.0.0.0_x64__abc", DisplayName = "Contoso" };
        var vm = BuildVm(executor, remover, appx: new[] { package });

        await vm.LoadAsync();
        SelectFirstAppx(vm);

        vm.RemoveAppxCommand.Execute(null);
        Assert.True(vm.RequiresConfirmation); // still gated behind confirm

        vm.ApproveCommand.Execute(null);
        await PumpAsync(() => vm.HasResult); // settle the full approve→remove→render before asserting

        Assert.Equal(0, executor.CallCount);              // never the typed-action executor
        Assert.Equal(1, remover.CallCount);
        Assert.True(vm.HasResult);
        Assert.DoesNotContain(vm.AllRows, r => r.Appx is not null); // removed from the unified list on success
    }

    [Fact]
    public async Task Appx_removal_failure_keeps_the_app_in_the_list()
    {
        var remover = new FakeAppxRemover { Result = new AppxRemovalResult(false, "refused") };
        var package = new InstalledAppx { PackageFullName = "Contoso.App_1.0.0.0_x64__abc", DisplayName = "Contoso" };
        var vm = BuildVm(new FakeExecutor(), remover, appx: new[] { package });

        await vm.LoadAsync();
        SelectFirstAppx(vm);
        vm.RemoveAppxCommand.Execute(null);
        vm.ApproveCommand.Execute(null);
        await PumpAsync(() => vm.HasResult); // settle the full approve→remove→render before asserting

        Assert.Equal(1, remover.CallCount);
        Assert.True(vm.HasResult);
        Assert.Single(vm.AllRows, r => r.Appx is not null); // not removed — still in the unified list
    }

    /// <summary>
    /// A probe whose single leftover is a genuinely ProgramOwned candidate (the exact
    /// Software\&lt;Publisher&gt;\&lt;DisplayName&gt; vendor leaf, in the app's own hive). This is what now flows into
    /// the deletable plan — a leftover FOLDER is classified Shared (PR-3 A) and would yield an empty plan.
    /// </summary>
    private static FakeLeftoverProbe ProbeWithProgramOwnedLeftover()
    {
        var probe = new FakeLeftoverProbe();
        probe.RegistryKeys.Add(new LeftoverRegistryKey(RegistryHive.LocalMachine,
            @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64, "vendor leaf (program-owned)"));
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

    private sealed class FakeFolderOpener : IFolderOpener
    {
        public string? LastPath { get; private set; }
        public void OpenFolder(string path) => LastPath = path; // never launches Explorer in tests
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

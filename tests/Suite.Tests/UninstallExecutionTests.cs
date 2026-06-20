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
/// The Sil execution wiring for the paths UninstallViewModel still owns: AppX removal is gated behind an
/// explicit confirm and goes through <see cref="IAppxRemover"/>, never the typed-action executor. The
/// path-independent FAIL-WITHOUT proof shows the classifier filter is what keeps a Shared key out of the
/// adapter. (The desktop official-run wiring now lives in UninstallWizardTests.) No real OS destruction
/// happens — everything is faked (spec §1.1, §3).
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

    /// <summary>Selects the unified-grid row backed by the first Store app.</summary>
    private static void SelectFirstAppx(UninstallViewModel vm)
        => vm.SelectedRow = vm.AllRows.First(r => r.Appx is not null);

    // Generous ceiling (flaky-fix 2026-06-21): the happy path exits the instant until() is true (~ms),
    // so a large cap never slows passing tests; it only prevents false "did not complete in time"
    // failures when the async ViewModel settle runs slow under CI/Release load (was 2000ms → flaked).
    private static async Task PumpAsync(Func<bool> until, int timeoutMs = 30_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!until() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10);
        Assert.True(until(), "operation did not complete in time");
    }

    // NOTE: UninstallViewModel's own official-uninstaller staging (RunOfficialCommand / StageOfficial /
    // OfficialActions / RunPlanAsync) was removed in PR-4 — the desktop official run is owned ENTIRELY by the
    // wizard. Its execution-wiring coverage (stage-does-not-execute, cancel-clears, approve-runs-the-exact-hash,
    // command-action plan) lives in UninstallWizardTests: RunOfficial_stages_a_command_plan_through_the_gate_
    // without_executing, Approving_official_runs_the_command_plan_then_advances_to_scan, and
    // Broken_uninstaller_app_cannot_run_official_and_offers_a_skip.

    // NOTE: the legacy UninstallViewModel leftover-deletion path was removed (PR-4 fix #1). Its two
    // integration tests (Real_gated_executor_dispatches_a_program_owned_leftover... and
    // Shared_and_protected_keys_never_reach_the_recording_registry_adapter) are migrated to
    // UninstallWizardTests.Shared_and_protected_never_reach_the_recording_adapter_via_the_wizard, which now
    // covers the end-to-end Shared/Protected-never-deleted safety through the SINGLE leftover-deletion path.
    // The FAIL-WITHOUT counterpart below is path-independent (it builds a raw plan directly), so it stays.

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

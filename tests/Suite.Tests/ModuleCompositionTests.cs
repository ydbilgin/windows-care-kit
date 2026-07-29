using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Controls;
using WindowsCareKit.App.Deployment;
using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.App.Views;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Win32;
using Xunit;
using WpfApp = WindowsCareKit.App.App;

namespace WindowsCareKit.Tests;

[Collection(WpfResourceCollection.Name)]
public sealed class ModuleCompositionTests
{
    [Fact]
    public void MainViewModel_builds_nav_from_module_set_and_only_constructs_supplied_content()
    {
        using ServiceProvider provider = BuildProvider(WpfApp.CreateDefaultModules());
        var vm = provider.GetRequiredService<MainViewModel>();

        Assert.Equal(
            new[] { "uninstall", "clean", "backup", "migration", "restore", "install", "settings" },
            vm.Nav.Select(item => item.Id).ToArray());
        Assert.Equal(
            new[] { "nav.uninstall", "nav.clean", "nav.backup", "nav.migration", "nav.restore", "nav.install", "nav.settings" },
            vm.Nav.Select(item => item.NameKey).ToArray());
        Assert.Equal(
            new[] { "\uE74D", "\uE75C", "\uE74E", "\uE7AD", "\uE81C", "\uE896", "\uE713" },
            vm.Nav.Select(item => item.Glyph).ToArray());
        Assert.DoesNotContain(vm.Nav.Take(6), item => item.IsSettings);
        Assert.True(vm.Nav.Last().IsSettings);
        Assert.IsType<UninstallViewModel>(vm.Nav[0].Content);
        Assert.IsType<SettingsViewModel>(vm.Nav[6].Content);

        var constructed = new List<string>();
        object clean = new();
        object backup = new();
        object settings = new();
        var subset = new IWckModule[]
        {
            TestModule.For("backup", "nav.backup", "nav.backup.desc", "\uE74E", 30, backup, constructed),
            TestModule.For("clean", "nav.clean", "nav.clean.desc", "\uE75C", 20, clean, constructed),
            TestModule.For("settings", "nav.settings", "nav.settings.desc", "\uE713", 900, settings, constructed, isSettings: true),
        };

        var subsetVm = new MainViewModel(new I18n(), subset);

        Assert.Equal(new[] { "clean", "backup", "settings" }, subsetVm.Nav.Select(item => item.Id).ToArray());
        Assert.Equal(new[] { "clean", "backup", "settings" }, constructed.ToArray());
        Assert.DoesNotContain(subsetVm.Nav, item => item.Id == "migration");

        var navAware = new RecordingNavigationAware();
        var navAwareVm = new MainViewModel(new I18n(), new IWckModule[]
        {
            TestModule.For("clean", "nav.clean", "nav.clean.desc", "\uE75C", 20, new object(), new List<string>()),
            new TestModule("migration", "nav.migration", "nav.migration.desc", "\uE7AD", 40, false, _ => navAware),
        });

        Assert.Equal(0, navAware.NavigatedToCount);
        Assert.True(navAwareVm.SelectNavByKey("migration"));
        Assert.Equal(1, navAware.NavigatedToCount);
    }

    [Fact]
    public void Payload_root_policy_is_registered_and_forbids_the_resolved_app_root()
    {
        var services = new ServiceCollection();
        WpfApp.AddBaseServices(services, []);
        using ServiceProvider provider = services.BuildServiceProvider();

        PayloadRootPolicy policy = provider.GetRequiredService<PayloadRootPolicy>();

        Assert.Contains(
            AppLayout.Current.Root,
            policy.ForbiddenRoots,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Navigating_away_cancels_the_active_navigation_load_and_the_shell_observes_completion()
    {
        var blocking = new BlockingNavigationAware();
        var vm = new MainViewModel(new I18n(), new IWckModule[]
        {
            TestModule.For("clean", "nav.clean", "nav.clean.desc", "", 20, new object(), new List<string>()),
            new TestModule("migration", "nav.migration", "nav.migration.desc", "", 40, false, _ => blocking),
        });

        // Constructor selected the first, non-nav-aware tab (clean, order 20): no load started.
        Assert.Equal(0, blocking.StartedCount);
        Assert.True(vm.ActiveNavigationTask.IsCompleted);

        Assert.True(vm.SelectNavByKey("migration"));
        Assert.Equal(1, blocking.StartedCount);
        Assert.False(blocking.SawCancellation);
        Assert.False(vm.ActiveNavigationTask.IsCompleted);   // the shell retained the still-running load

        Assert.True(vm.SelectNavByKey("clean"));             // navigate away
        await vm.ActiveNavigationTask;                        // completion is observable, not discarded

        Assert.True(blocking.SawCancellation);               // cancellation reached the module
        Assert.True(blocking.ObservedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_navigation_load_that_fails_after_yielding_is_observed_without_faulting_the_shell()
    {
        var faulting = new ControllableFaultingNavigationAware();
        var vm = new MainViewModel(new I18n(), new IWckModule[]
        {
            TestModule.For("clean", "nav.clean", "nav.clean.desc", "", 20, new object(), new List<string>()),
            new TestModule("migration", "nav.migration", "nav.migration.desc", "", 40, false, _ => faulting),
        });

        Exception? thrown = Record.Exception(() => { vm.SelectNavByKey("migration"); });
        Assert.Null(thrown);                                  // the fault never escapes the synchronous setter

        // Proves the shell is genuinely TRACKING this load, not a stale already-completed sentinel: a
        // regression that discarded the returned task would leave ActiveNavigationTask already-completed here.
        Assert.False(vm.ActiveNavigationTask.IsCompleted);

        faulting.ReleaseFault();                              // let the pending load fault, now that it was observed pending
        await vm.ActiveNavigationTask;                        // the shell observed it (swallow + Trace), task completes
        Assert.Equal(TaskStatus.RanToCompletion, vm.ActiveNavigationTask.Status);
    }

    [Fact]
    public async Task RetireNavigation_never_clobbers_a_newer_navigations_cts_when_an_install_is_forced_into_its_exact_race_window()
    {
        // Deterministic race, not a timing gamble: MainViewModel.RaceTestHook_AfterRetireOwnershipRead lets this
        // test pause an OLD navigation's retirement at exactly the point the MAJOR finding describes — AFTER
        // that retirement has genuinely observed (via Volatile.Read) "yes, I still own this CTS", but BEFORE its
        // atomic clear takes effect. Then a NEWER navigation's CTS is installed while the old retirement is
        // paused in that exact post-observation window, and only then is the old retirement released to
        // perform its clear. This reproduces the historical bug's real shape: old retirement observes ownership
        // as true → newer navigation installs → old retirement's clear attempt must not clobber the newer
        // reference. A stale ReferenceEquals-then-plain-assign observes "still active" as true during the same
        // window and then unconditionally clobbers the newer reference with null once released.
        // Interlocked.CompareExchange resolves against whatever is CURRENT at the instant it actually runs (even
        // though that instant was deliberately forced to be strictly after the competing install), so it must
        // correctly refuse to clear the newer CTS.
        var oldLoad = new BlockingNavigationAware();
        var newLoad = new BlockingNavigationAware();
        var vm = new MainViewModel(new I18n(), new IWckModule[]
        {
            TestModule.For("clean", "nav.clean", "nav.clean.desc", "", 20, new object(), new List<string>()),
            new TestModule("migration", "nav.migration", "nav.migration.desc", "", 40, false, _ => oldLoad),
            new TestModule("restore", "nav.restore", "nav.restore.desc", "", 45, false, _ => newLoad),
        });

        Assert.True(vm.SelectNavByKey("migration"));
        Task oldNavigationTask = vm.ActiveNavigationTask;

        using var oldRetirementPaused = new ManualResetEventSlim(false);
        using var releaseOldRetirement = new ManualResetEventSlim(false);
        vm.RaceTestHook_AfterRetireOwnershipRead = () =>
        {
            oldRetirementPaused.Set();
            releaseOldRetirement.Wait(TimeSpan.FromSeconds(5));
        };

        oldLoad.Complete(); // lets the old load finish; its retirement continuation will hit the hook and pause

        Assert.True(oldRetirementPaused.Wait(TimeSpan.FromSeconds(2))); // old retirement is now paused, mid-retire

        // Install a NEW navigation now — while the old retirement is paused exactly at "about to clear".
        Assert.True(vm.SelectNavByKey("restore"));

        releaseOldRetirement.Set(); // let the old retirement's atomic clear finally run
        await oldNavigationTask;

        System.Reflection.FieldInfo ctsField = typeof(MainViewModel).GetField(
            "_navigationCts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var currentCts = (CancellationTokenSource?)ctsField.GetValue(vm);
        Assert.NotNull(currentCts);                        // the NEW navigation's CTS must survive the race
        Assert.False(currentCts!.IsCancellationRequested);  // not cancelled/cleared by the older retirement
        Assert.Equal(1, newLoad.StartedCount);
        Assert.False(newLoad.SawCancellation);              // the new load must still be genuinely running

        // Navigating away from the new (still-active) load must still be able to cancel it — proving the
        // shell's field still references it (it was not nulled/disposed by the older retirement).
        Assert.True(vm.SelectNavByKey("clean"));
        await vm.ActiveNavigationTask;
        Assert.True(newLoad.SawCancellation);
        Assert.True(newLoad.ObservedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_navigation_aware_initial_tab_starts_its_load_during_construction_and_it_is_observable()
    {
        var blocking = new BlockingNavigationAware();
        var vm = new MainViewModel(new I18n(), new IWckModule[]
        {
            new TestModule("migration", "nav.migration", "nav.migration.desc", "", 5, false, _ => blocking),
            TestModule.For("clean", "nav.clean", "nav.clean.desc", "", 20, new object(), new List<string>()),
        });

        Assert.Equal("migration", vm.Nav[0].Id);              // the nav-aware tab sorts first (order 5)
        Assert.Equal(1, blocking.StartedCount);               // its load started during construction
        Assert.False(vm.ActiveNavigationTask.IsCompleted);    // retained and observable, not discarded

        blocking.Complete();
        await vm.ActiveNavigationTask;                         // drains cleanly (RanToCompletion), no leaked task
        Assert.Equal(TaskStatus.RanToCompletion, vm.ActiveNavigationTask.Status);
    }

    [Fact]
    public async Task OnShellStartup_is_safe_when_uninstall_module_is_absent_and_invokes_only_startup_aware_content()
    {
        var subsetConstructed = new List<string>();
        var subset = new IWckModule[]
        {
            TestModule.For("clean", "nav.clean", "nav.clean.desc", "", 20, new object(), subsetConstructed),
            TestModule.For("settings", "nav.settings", "nav.settings.desc", "", 900, new object(), subsetConstructed, isSettings: true),
        };
        var subsetVm = new MainViewModel(new I18n(), subset);

        Exception? thrown = await Record.ExceptionAsync(subsetVm.OnShellStartupAsync);
        Assert.Null(thrown);

        var startupAware = new RecordingStartupAware();
        var mixedConstructed = new List<string>();
        var mixed = new IWckModule[]
        {
            TestModule.For("clean", "nav.clean", "nav.clean.desc", "", 20, new object(), mixedConstructed),
            new TestModule("migration", "nav.migration", "nav.migration.desc", "", 40, false, _ => startupAware),
        };
        var mixedVm = new MainViewModel(new I18n(), mixed);

        await mixedVm.OnShellStartupAsync();

        Assert.Equal(1, startupAware.StartupCount);

        var faultingVm = new MainViewModel(new I18n(), new IWckModule[]
        {
            new TestModule("uninstall", "nav.uninstall", "nav.uninstall.desc", "", 10, false, _ => new FaultingStartupAware()),
        });
        InvalidOperationException fault = await Assert.ThrowsAsync<InvalidOperationException>(
            faultingVm.OnShellStartupAsync);
        Assert.Equal("synthetic startup failure", fault.Message);
    }

    [Fact]
    public void Shell_startup_exposes_an_observable_task_instead_of_discarding_module_faults()
    {
        System.Reflection.MethodInfo? startup = typeof(MainViewModel).GetMethod("OnShellStartupAsync");

        Assert.NotNull(startup);
        Assert.Equal(typeof(Task), startup.ReturnType);

        string shellSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Suite.App.Wpf",
            "ViewModels",
            "MainViewModel.cs"));
        Assert.DoesNotContain("_ = aware.OnShellStartupAsync()", shellSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_catalog_has_uninstall_as_the_only_startup_aware_nav_content()
    {
        using ServiceProvider provider = BuildProvider(WpfApp.CreateDefaultModules());
        var vm = provider.GetRequiredService<MainViewModel>();

        List<NavItem> startupAwareItems = vm.Nav.Where(item => item.Content is IWckStartupAware).ToList();

        NavItem only = Assert.Single(startupAwareItems);
        Assert.Equal("uninstall", only.Id);
        Assert.IsType<UninstallViewModel>(only.Content);
    }

    [Fact]
    public void MigrationModule_creates_content_and_view_from_migration_assembly_and_registers_only_migration_services()
    {
        RunOnStaThread(() =>
        {
            var baseServices = new ServiceCollection();
            WpfApp.AddBaseServices(baseServices, Array.Empty<string>());
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IInstalledAppReader));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IAppxReader));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IRegistryProbe));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(ICurrentSidProvider));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IBackupExecutor));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IHasher));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IFileSystem));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IClock));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(MigrationRestoreManifestStore));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(IMsiCatalog));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(IMigrationScanService));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(IRecipeFileSystem));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(IContentSignatureProbe));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(MigrationViewModel));
            using ServiceProvider baseProvider = baseServices.BuildServiceProvider();

            Assert.NotNull(baseProvider.GetService<I18n>());
            Assert.NotNull(baseProvider.GetService<ISafetyGate>());
            Assert.NotNull(baseProvider.GetService<IInstalledAppReader>());
            Assert.NotNull(baseProvider.GetService<IAppxReader>());
            Assert.NotNull(baseProvider.GetService<IRegistryProbe>());
            Assert.NotNull(baseProvider.GetService<ICurrentSidProvider>());
            Assert.NotNull(baseProvider.GetService<IBackupExecutor>());
            Assert.NotNull(baseProvider.GetService<IHasher>());
            Assert.NotNull(baseProvider.GetService<IFileSystem>());
            Assert.NotNull(baseProvider.GetService<IClock>());
            Assert.NotNull(baseProvider.GetService<MigrationRestoreManifestStore>());
            Assert.Null(baseProvider.GetService<IMsiCatalog>());
            Assert.Null(baseProvider.GetService<IMigrationScanService>());
            Assert.Null(baseProvider.GetService<IRecipeFileSystem>());
            Assert.Null(baseProvider.GetService<IContentSignatureProbe>());
            Assert.Empty(baseProvider.GetServices<IProgramSource>());
            Assert.Null(baseProvider.GetService<MigrationViewModel>());

            var services = new ServiceCollection();
            WpfApp.AddBaseServices(services, Array.Empty<string>());
            var module = new MigrationModule();
            module.RegisterServices(services);
            using ServiceProvider provider = services.BuildServiceProvider();

            object content = module.CreateContent(provider);
            FrameworkElement view = Assert.IsAssignableFrom<FrameworkElement>(module.CreateView());

            var vm = Assert.IsType<MigrationViewModel>(content);
            var migrationView = Assert.IsType<MigrationView>(view);
            Assert.Equal("Suite.Module.Migration", module.GetType().Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Migration", vm.GetType().Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Migration", migrationView.GetType().Assembly.GetName().Name);
            Assert.IsType<Win32MsiCatalog>(provider.GetRequiredService<IMsiCatalog>());
            Assert.IsType<Win32StartMenuShortcutReader>(provider.GetRequiredService<IStartMenuShortcutReader>());
            Assert.IsType<Win32RecipeFileSystem>(provider.GetRequiredService<IRecipeFileSystem>());
            Assert.IsType<Win32ContentSignatureProbe>(provider.GetRequiredService<IContentSignatureProbe>());
            Assert.Equal(5, provider.GetServices<IProgramSource>().Count());
            Assert.NotNull(provider.GetRequiredService<IMigrationScanService>());
            Assert.NotNull(provider.GetRequiredService<RecipeResolver>());
            Assert.NotNull(provider.GetRequiredService<MigrationInstallManifestStore>());
            Assert.NotNull(provider.GetRequiredService<MigrationBackupRunner>());
            Assert.NotNull(provider.GetRequiredService<IMigrationBackupRunner>());
            Assert.Equal(40, provider.GetRequiredService<Func<IReadOnlyList<MigrationRecipe>>>()().Count);

            // i18n fragment ownership (modular M2b, SPEC §D3): migration.restore.* belongs to Restore,
            // NOT Migration, even though the prefix says otherwise.
            IReadOnlyDictionary<string, string> migrationEn = ((IWckModule)module).GetLangFragment("en");
            IReadOnlyDictionary<string, string> migrationTr = ((IWckModule)module).GetLangFragment("tr");
            Assert.Contains("nav.migration", migrationEn.Keys);
            Assert.Contains("migration.title", migrationEn.Keys);
            Assert.DoesNotContain("migration.restore.title", migrationEn.Keys);
            Assert.Equal(migrationEn.Keys.Order(StringComparer.Ordinal), migrationTr.Keys.Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public void CleanModule_creates_content_and_view_from_clean_assembly_and_registers_win32_probes()
    {
        RunOnStaThread(() =>
        {
            var services = new ServiceCollection();
            WpfApp.AddBaseServices(services, Array.Empty<string>());
            var module = new CleanModule();
            module.RegisterServices(services);
            using ServiceProvider provider = services.BuildServiceProvider();

            object content = module.CreateContent(provider);
            FrameworkElement view = Assert.IsAssignableFrom<FrameworkElement>(module.CreateView());

            var vm = Assert.IsType<CleanViewModel>(content);
            var cleanView = Assert.IsType<CleanView>(view);
            Assert.Equal("Suite.Module.Clean", module.GetType().Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Clean", vm.GetType().Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Clean", cleanView.GetType().Assembly.GetName().Name);
            Assert.IsType<Win32JunkProbe>(provider.GetRequiredService<IJunkProbe>());
            Assert.IsType<Win32StartupProbe>(provider.GetRequiredService<IStartupProbe>());
            Assert.IsType<Win32BrowserExtensionInventory>(provider.GetRequiredService<IBrowserExtensionInventory>());
            Assert.IsType<Win32RecycleBinService>(provider.GetRequiredService<IRecycleBinService>());
            Assert.NotNull(provider.GetRequiredService<IPlanExecutor>());

            // i18n fragment ownership (modular M2b, SPEC §D3): uninstall.leftovers.skippedTitle belongs to
            // Clean (sole consumer CleanView.xaml), NOT Uninstall, even though the prefix says otherwise.
            IReadOnlyDictionary<string, string> cleanEn = ((IWckModule)module).GetLangFragment("en");
            IReadOnlyDictionary<string, string> cleanTr = ((IWckModule)module).GetLangFragment("tr");
            Assert.Contains("nav.clean", cleanEn.Keys);
            Assert.Contains("uninstall.leftovers.skippedTitle", cleanEn.Keys);
            Assert.Equal(cleanEn.Keys.Order(StringComparer.Ordinal), cleanTr.Keys.Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public void BackupModule_creates_content_and_view_from_backup_assembly_and_registers_only_backup_services()
    {
        RunOnStaThread(() =>
        {
            var baseServices = new ServiceCollection();
            WpfApp.AddBaseServices(baseServices, Array.Empty<string>());
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IBackupExecutor));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IClock));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IHasher));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IFileSystem));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(IEnvironmentExpander));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(IManifestLoader));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(BackupPlanner));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(BackupReportWriter));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(IIntegrityWriter));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(BackupRunner));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(BackupViewModel));
            using ServiceProvider baseProvider = baseServices.BuildServiceProvider();

            Assert.NotNull(baseProvider.GetService<I18n>());
            Assert.NotNull(baseProvider.GetService<ISafetyGate>());
            Assert.NotNull(baseProvider.GetService<IBackupExecutor>());
            Assert.NotNull(baseProvider.GetService<IClock>());
            Assert.NotNull(baseProvider.GetService<IHasher>());
            Assert.NotNull(baseProvider.GetService<IFileSystem>());
            Assert.Null(baseProvider.GetService<IEnvironmentExpander>());
            Assert.Null(baseProvider.GetService<IManifestLoader>());
            Assert.Null(baseProvider.GetService<BackupPlanner>());
            Assert.Null(baseProvider.GetService<BackupReportWriter>());
            Assert.Null(baseProvider.GetService<IIntegrityWriter>());
            Assert.Null(baseProvider.GetService<BackupRunner>());
            Assert.Null(baseProvider.GetService<BackupViewModel>());

            var services = new ServiceCollection();
            WpfApp.AddBaseServices(services, Array.Empty<string>());
            var module = new BackupModule();
            module.RegisterServices(services);
            using ServiceProvider provider = services.BuildServiceProvider();

            object content = module.CreateContent(provider);
            FrameworkElement view = Assert.IsAssignableFrom<FrameworkElement>(module.CreateView());

            var vm = Assert.IsType<BackupViewModel>(content);
            var backupView = Assert.IsType<BackupView>(view);
            Assert.Equal("Suite.Module.Backup", module.GetType().Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Backup", vm.GetType().Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Backup", backupView.GetType().Assembly.GetName().Name);
            Assert.IsType<Win32EnvironmentExpander>(provider.GetRequiredService<IEnvironmentExpander>());
            Assert.IsType<ManifestLoader>(provider.GetRequiredService<IManifestLoader>());
            Assert.NotNull(provider.GetRequiredService<BackupPlanner>());
            Assert.NotNull(provider.GetRequiredService<BackupReportWriter>());
            Assert.IsType<BackupIntegrityWriter>(provider.GetRequiredService<IIntegrityWriter>());
            Assert.NotNull(provider.GetRequiredService<BackupRunner>());
            Assert.NotNull(provider.GetRequiredService<IBackupExecutor>());
            Assert.NotNull(provider.GetRequiredService<IClock>());
            Assert.NotNull(provider.GetRequiredService<IHasher>());
            Assert.NotNull(provider.GetRequiredService<IFileSystem>());

            IReadOnlyDictionary<string, string> backupEn = ((IWckModule)module).GetLangFragment("en");
            IReadOnlyDictionary<string, string> backupTr = ((IWckModule)module).GetLangFragment("tr");
            Assert.Contains("nav.backup", backupEn.Keys);
            Assert.Equal(backupEn.Keys.Order(StringComparer.Ordinal), backupTr.Keys.Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public void UninstallModule_creates_content_and_view_from_uninstall_assembly_and_registers_only_uninstall_services()
    {
        RunOnStaThread(() =>
        {
            var baseServices = new ServiceCollection();
            WpfApp.AddBaseServices(baseServices, Array.Empty<string>());
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IInstalledAppReader));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IAppxReader));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IRegistryProbe));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IFolderOpener));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IExecutor));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IRestorePointCapabilityProbe));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(ILeftoverProbe));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(IAppxRemover));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(UninstallViewModel));
            using ServiceProvider baseProvider = baseServices.BuildServiceProvider();

            Assert.NotNull(baseProvider.GetService<I18n>());
            Assert.NotNull(baseProvider.GetService<ISafetyGate>());
            Assert.NotNull(baseProvider.GetService<IInstalledAppReader>());
            Assert.NotNull(baseProvider.GetService<IAppxReader>());
            Assert.NotNull(baseProvider.GetService<IRegistryProbe>());
            Assert.NotNull(baseProvider.GetService<IFolderOpener>());
            Assert.NotNull(baseProvider.GetService<IExecutor>());
            Assert.NotNull(baseProvider.GetService<IRestorePointCapabilityProbe>());
            Assert.Null(baseProvider.GetService<ILeftoverProbe>());
            Assert.Null(baseProvider.GetService<IAppxRemover>());
            Assert.Null(baseProvider.GetService<UninstallViewModel>());

            var services = new ServiceCollection();
            WpfApp.AddBaseServices(services, Array.Empty<string>());
            var module = new UninstallModule();
            module.RegisterServices(services);
            using ServiceProvider provider = services.BuildServiceProvider();

            object content = module.CreateContent(provider);
            FrameworkElement view = Assert.IsAssignableFrom<FrameworkElement>(module.CreateView());

            var vm = Assert.IsType<UninstallViewModel>(content);
            var uninstallView = Assert.IsType<UninstallView>(view);
            Assert.Equal("Suite.Module.Uninstall", module.GetType().Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Uninstall", vm.GetType().Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Uninstall", vm.Wizard.GetType().Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Uninstall", typeof(AppRow).Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Uninstall", typeof(LeftoverNode).Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Uninstall", uninstallView.GetType().Assembly.GetName().Name);
            Assert.IsType<Win32LeftoverProbe>(provider.GetRequiredService<ILeftoverProbe>());
            Assert.Null(provider.GetService<IAppxRemover>());
            Assert.NotNull(provider.GetRequiredService<IInstalledAppReader>());
            Assert.NotNull(provider.GetRequiredService<IAppxReader>());
            Assert.NotNull(provider.GetRequiredService<IRegistryProbe>());
            Assert.NotNull(provider.GetRequiredService<IFolderOpener>());
            Assert.NotNull(provider.GetRequiredService<IExecutor>());
            Assert.NotNull(provider.GetRequiredService<IRestorePointCapabilityProbe>());

            // i18n fragment ownership (modular M2b): uninstall.leftovers.skippedTitle moved to Clean —
            // Uninstall's own fragment must not carry it.
            IReadOnlyDictionary<string, string> uninstallEn = ((IWckModule)module).GetLangFragment("en");
            IReadOnlyDictionary<string, string> uninstallTr = ((IWckModule)module).GetLangFragment("tr");
            Assert.Contains("nav.uninstall", uninstallEn.Keys);
            Assert.DoesNotContain("uninstall.leftovers.skippedTitle", uninstallEn.Keys);
            Assert.Equal(uninstallEn.Keys.Order(StringComparer.Ordinal), uninstallTr.Keys.Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public void ConfirmGate_types_live_in_app_abstractions()
    {
        Assert.Equal("Suite.App.Abstractions", typeof(ConfirmGateViewModel).Assembly.GetName().Name);
        Assert.Equal("Suite.App.Abstractions", typeof(ConfirmGate).Assembly.GetName().Name);
    }

    [Fact]
    public void InstallModule_creates_content_and_view_from_install_assembly_and_registers_only_install_services()
    {
        RunOnStaThread(() =>
        {
            var baseServices = new ServiceCollection();
            WpfApp.AddBaseServices(baseServices, Array.Empty<string>());
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(InstallPlanner));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(IInstallManifestLoader));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(IAuthProbe));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(IDriverGuard));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(IInstallPlanWriter));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(InstallRunner));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(InstallViewModel));
            using ServiceProvider baseProvider = baseServices.BuildServiceProvider();

            Assert.NotNull(baseProvider.GetService<I18n>());
            Assert.NotNull(baseProvider.GetService<ISafetyGate>());
            Assert.NotNull(baseProvider.GetService<IRestoreStateStore>());
            Assert.Null(baseProvider.GetService<IInstallManifestLoader>());
            Assert.Null(baseProvider.GetService<IAuthProbe>());
            Assert.Null(baseProvider.GetService<IDriverGuard>());
            Assert.Null(baseProvider.GetService<IInstallPlanWriter>());
            Assert.Null(baseProvider.GetService<InstallRunner>());
            Assert.Null(baseProvider.GetService<InstallViewModel>());
            Assert.Null(baseProvider.GetService<InstallPlanner>());

            var services = new ServiceCollection();
            WpfApp.AddBaseServices(services, Array.Empty<string>());
            var module = new InstallModule();
            module.RegisterServices(services);
            using ServiceProvider provider = services.BuildServiceProvider();

            object content = module.CreateContent(provider);
            FrameworkElement view = Assert.IsAssignableFrom<FrameworkElement>(module.CreateView());

            var vm = Assert.IsType<InstallViewModel>(content);
            var installView = Assert.IsType<InstallView>(view);
            Assert.Equal("Suite.Module.Install", module.GetType().Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Install", vm.GetType().Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Install", installView.GetType().Assembly.GetName().Name);
            Assert.IsType<InstallManifestLoader>(provider.GetRequiredService<IInstallManifestLoader>());
            Assert.IsType<Win32AuthProbe>(provider.GetRequiredService<IAuthProbe>());
            Assert.IsType<Win32DriverGuard>(provider.GetRequiredService<IDriverGuard>());
            Assert.IsType<InstallPlanWriter>(provider.GetRequiredService<IInstallPlanWriter>());
            Assert.NotNull(provider.GetRequiredService<InstallRunner>());
            Assert.NotNull(provider.GetRequiredService<InstallPlanner>());
            Assert.NotNull(provider.GetRequiredService<IPlanExecutor>());

            IReadOnlyDictionary<string, string> installEn = ((IWckModule)module).GetLangFragment("en");
            IReadOnlyDictionary<string, string> installTr = ((IWckModule)module).GetLangFragment("tr");
            Assert.Contains("nav.install", installEn.Keys);
            Assert.Equal(installEn.Keys.Order(StringComparer.Ordinal), installTr.Keys.Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public void RestoreModule_creates_content_and_view_from_restore_assembly_and_registers_only_restore_services()
    {
        RunOnStaThread(() =>
        {
            var baseServices = new ServiceCollection();
            WpfApp.AddBaseServices(baseServices, Array.Empty<string>());
            Assert.Contains(baseServices, d => d.ServiceType == typeof(MigrationRestoreManifestStore));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IRestoreStateStore));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(GatedExecutor));
            // Round 2 (DIP-03): the concrete MigrationRestoreService + its IMigrationRestoreService port
            // adapter are now composed at the root (AddBaseServices), not inside RestoreModule, so the
            // Restore module itself no longer needs to reference Suite.Execution. Only RestoreViewModel
            // stays module-owned.
            Assert.Contains(baseServices, d => d.ServiceType == typeof(MigrationRestoreService));
            Assert.Contains(baseServices, d => d.ServiceType == typeof(IMigrationRestoreService));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(RestoreViewModel));
            using ServiceProvider baseProvider = baseServices.BuildServiceProvider();

            Assert.NotNull(baseProvider.GetService<I18n>());
            Assert.NotNull(baseProvider.GetService<ISafetyGate>());
            Assert.NotNull(baseProvider.GetService<MigrationRestoreManifestStore>());
            Assert.NotNull(baseProvider.GetService<IRestoreStateStore>());
            Assert.NotNull(baseProvider.GetService<GatedExecutor>());
            Assert.NotNull(baseProvider.GetService<MigrationRestoreService>());
            Assert.NotNull(baseProvider.GetService<IMigrationRestoreService>());
            Assert.Null(baseProvider.GetService<RestoreViewModel>());

            var services = new ServiceCollection();
            WpfApp.AddBaseServices(services, Array.Empty<string>());
            var module = new RestoreModule();
            module.RegisterServices(services);
            using ServiceProvider provider = services.BuildServiceProvider();

            object content = module.CreateContent(provider);
            FrameworkElement view = Assert.IsAssignableFrom<FrameworkElement>(module.CreateView());

            var vm = Assert.IsType<RestoreViewModel>(content);
            var restoreView = Assert.IsType<RestoreView>(view);
            Assert.Equal("Suite.Module.Restore", module.GetType().Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Restore", vm.GetType().Assembly.GetName().Name);
            Assert.Equal("Suite.Module.Restore", restoreView.GetType().Assembly.GetName().Name);
            Assert.NotNull(provider.GetRequiredService<MigrationRestoreService>());
            Assert.NotNull(provider.GetRequiredService<MigrationRestoreManifestStore>());
            Assert.NotNull(provider.GetRequiredService<IRestoreStateStore>());
            Assert.NotNull(provider.GetRequiredService<GatedExecutor>());
            Assert.Equal("Suite.Execution", typeof(MigrationRestoreExecutionResult).Assembly.GetName().Name);
            Assert.Equal("Suite.Execution", typeof(MigrationRestorePreviewResult).Assembly.GetName().Name);
            Assert.Equal("Suite.Execution", typeof(MigrationRestoreUndoResult).Assembly.GetName().Name);
            Assert.Equal("Suite.Execution", typeof(MigrationRestoreUndoPreviewResult).Assembly.GetName().Name);

            // i18n fragment ownership (modular M2b, SPEC §D3): migration.restore.* belongs to Restore,
            // NOT Migration, even though the prefix says otherwise.
            IReadOnlyDictionary<string, string> restoreEn = ((IWckModule)module).GetLangFragment("en");
            IReadOnlyDictionary<string, string> restoreTr = ((IWckModule)module).GetLangFragment("tr");
            Assert.Contains("nav.restore", restoreEn.Keys);
            Assert.Contains("migration.restore.title", restoreEn.Keys);
            Assert.DoesNotContain("migration.title", restoreEn.Keys);
            Assert.Equal(restoreEn.Keys.Order(StringComparer.Ordinal), restoreTr.Keys.Order(StringComparer.Ordinal));
        });
    }

    private static ServiceProvider BuildProvider(IReadOnlyList<IWckModule> modules)
    {
        var services = new ServiceCollection();
        WpfApp.AddBaseServices(services, Array.Empty<string>());
        foreach (IWckModule module in modules)
            module.RegisterServices(services);
        services.AddSingleton(modules);
        return services.BuildServiceProvider();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "WindowsCareKit.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate WindowsCareKit.slnx.");
    }

    private sealed class TestModule(
        string id,
        string titleKey,
        string descKey,
        string iconKey,
        int order,
        bool isSettings,
        Func<IServiceProvider, object> contentFactory) : IWckModule
    {
        public string Id => id;
        public string TitleKey => titleKey;
        public string DescKey => descKey;
        public string IconKey => iconKey;
        public int Order => order;
        public bool IsSettings => isSettings;

        public static TestModule For(
            string id,
            string titleKey,
            string descKey,
            string iconKey,
            int order,
            object content,
            List<string> constructed,
            bool isSettings = false)
            => new(id, titleKey, descKey, iconKey, order, isSettings, _ =>
            {
                constructed.Add(id);
                return content;
            });

        public void RegisterServices(IServiceCollection services)
        {
        }

        public object CreateContent(IServiceProvider sp) => contentFactory(sp);

        public FrameworkElement? CreateView() => null;
    }

    private sealed class RecordingNavigationAware : IWckNavigationAware
    {
        public int NavigatedToCount { get; private set; }

        public Task OnNavigatedToAsync(CancellationToken cancellationToken)
        {
            NavigatedToCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingNavigationAware : IWckNavigationAware
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartedCount { get; private set; }
        public bool SawCancellation { get; private set; }
        public CancellationToken ObservedToken { get; private set; }

        public async Task OnNavigatedToAsync(CancellationToken cancellationToken)
        {
            StartedCount++;
            ObservedToken = cancellationToken;
            using CancellationTokenRegistration reg = cancellationToken.Register(() =>
            {
                SawCancellation = true;
                _release.TrySetResult();
            });
            await _release.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>Let the load finish successfully (used by the initial-tab test so no running task leaks).</summary>
        public void Complete() => _release.TrySetResult();
    }

    private sealed class ControllableFaultingNavigationAware : IWckNavigationAware
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OnNavigatedToAsync(CancellationToken cancellationToken)
        {
            await _release.Task.ConfigureAwait(false);
            throw new InvalidOperationException("synthetic navigation failure");
        }

        /// <summary>Let the pending load fault now (used so the test can first prove the shell was genuinely
        /// tracking this load while pending, not a discarded task).</summary>
        public void ReleaseFault() => _release.TrySetResult();
    }

    private sealed class RecordingStartupAware : IWckStartupAware
    {
        public int StartupCount { get; private set; }

        public Task OnShellStartupAsync()
        {
            StartupCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FaultingStartupAware : IWckStartupAware
    {
        public async Task OnShellStartupAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException("synthetic startup failure");
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

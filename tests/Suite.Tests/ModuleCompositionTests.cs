using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Controls;
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
    public void OnShellStartup_is_safe_when_uninstall_module_is_absent_and_invokes_only_startup_aware_content()
    {
        var subsetConstructed = new List<string>();
        var subset = new IWckModule[]
        {
            TestModule.For("clean", "nav.clean", "nav.clean.desc", "", 20, new object(), subsetConstructed),
            TestModule.For("settings", "nav.settings", "nav.settings.desc", "", 900, new object(), subsetConstructed, isSettings: true),
        };
        var subsetVm = new MainViewModel(new I18n(), subset);

        Exception? thrown = Record.Exception(() => subsetVm.OnShellStartup());
        Assert.Null(thrown);

        var startupAware = new RecordingStartupAware();
        var mixedConstructed = new List<string>();
        var mixed = new IWckModule[]
        {
            TestModule.For("clean", "nav.clean", "nav.clean.desc", "", 20, new object(), mixedConstructed),
            new TestModule("migration", "nav.migration", "nav.migration.desc", "", 40, false, _ => startupAware),
        };
        var mixedVm = new MainViewModel(new I18n(), mixed);

        mixedVm.OnShellStartup();

        Assert.Equal(1, startupAware.StartupCount);
    }

    [Fact]
    public void StaticModuleCatalog_LoadModules_yields_pinned_ids_in_order_with_existing_glyphs()
    {
        IReadOnlyList<IWckModule> modules = new StaticModuleCatalog().LoadModules();

        Assert.Equal(
            new[] { "uninstall", "clean", "backup", "migration", "restore", "install", "settings" },
            modules.Select(m => m.Id).ToArray());
        Assert.Equal(
            new[] { "", "", "", "", "", "", "" },
            modules.Select(m => m.IconKey).ToArray());
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
            Assert.IsType<Win32AppxRemover>(provider.GetRequiredService<IAppxRemover>());
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
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(MigrationRestoreService));
            Assert.DoesNotContain(baseServices, d => d.ServiceType == typeof(RestoreViewModel));
            using ServiceProvider baseProvider = baseServices.BuildServiceProvider();

            Assert.NotNull(baseProvider.GetService<I18n>());
            Assert.NotNull(baseProvider.GetService<ISafetyGate>());
            Assert.NotNull(baseProvider.GetService<MigrationRestoreManifestStore>());
            Assert.NotNull(baseProvider.GetService<IRestoreStateStore>());
            Assert.NotNull(baseProvider.GetService<GatedExecutor>());
            Assert.Null(baseProvider.GetService<MigrationRestoreService>());
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

        public void OnNavigatedTo() => NavigatedToCount++;
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

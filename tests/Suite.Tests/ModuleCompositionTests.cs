using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.App.Views;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Safety;
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
    public void MigrationModule_registration_is_isolated_from_base_and_other_modules()
    {
        var withMigration = new ServiceCollection();
        WpfApp.AddBaseServices(withMigration, Array.Empty<string>());
        new MigrationModule().RegisterServices(withMigration);
        using ServiceProvider migrationProvider = withMigration.BuildServiceProvider();

        Assert.NotNull(migrationProvider.GetService<I18n>());
        Assert.NotNull(migrationProvider.GetService<ISafetyGate>());
        Assert.NotNull(migrationProvider.GetService<IInstalledAppReader>());
        Assert.NotNull(migrationProvider.GetService<IMigrationScanService>());
        Assert.NotNull(migrationProvider.GetService<IRecipeFileSystem>());
        Assert.NotNull(migrationProvider.GetService<IContentSignatureProbe>());
        Assert.Equal(5, migrationProvider.GetServices<IProgramSource>().Count());
        Assert.Null(migrationProvider.GetService<ILeftoverProbe>());
        Assert.Null(migrationProvider.GetService<IJunkProbe>());
        Assert.Null(migrationProvider.GetService<IManifestLoader>());
        Assert.Null(migrationProvider.GetService<IInstallManifestLoader>());

        var withoutMigration = new ServiceCollection();
        WpfApp.AddBaseServices(withoutMigration, Array.Empty<string>());
        using ServiceProvider baseProvider = withoutMigration.BuildServiceProvider();

        Assert.NotNull(baseProvider.GetService<I18n>());
        Assert.NotNull(baseProvider.GetService<ISafetyGate>());
        Assert.NotNull(baseProvider.GetService<IInstalledAppReader>());
        Assert.Null(baseProvider.GetService<IMigrationScanService>());
        Assert.Null(baseProvider.GetService<IRecipeFileSystem>());
        Assert.Null(baseProvider.GetService<IContentSignatureProbe>());
        Assert.Empty(baseProvider.GetServices<IProgramSource>());
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

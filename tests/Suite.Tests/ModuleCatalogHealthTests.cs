using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using WindowsCareKit.App.Theming;
using WindowsCareKit.App.ViewModels;
using Xunit;
using WpfApp = WindowsCareKit.App.App;

namespace WindowsCareKit.Tests;

public sealed class ModuleCatalogHealthTests
{
    private const string ModulesRoot = @"C:\wck-test\Modules";

    [Fact]
    public void FromComponents_derives_not_installed_complete_and_degraded()
    {
        ModuleCatalogHealth notInstalled = ModuleCatalogHealth.FromComponents(ModulesRoot, []);
        ModuleCatalogHealth complete = ModuleCatalogHealth.FromComponents(
            ModulesRoot,
            [new("clean", ModuleComponentStatus.Loaded, null)]);

        Assert.Equal(ModuleInventoryStatus.NotInstalled, notInstalled.Status);
        Assert.Equal(ModuleInventoryStatus.Complete, complete.Status);

        foreach (ModuleComponentStatus failedStatus in new[]
                 {
                     ModuleComponentStatus.Incomplete,
                     ModuleComponentStatus.Malformed,
                     ModuleComponentStatus.Unreadable,
                 })
        {
            ModuleCatalogHealth degraded = ModuleCatalogHealth.FromComponents(
                ModulesRoot,
                [
                    new("clean", ModuleComponentStatus.Loaded, null),
                    new("failed", failedStatus, "Synthetic"),
                ]);

            Assert.Equal(ModuleInventoryStatus.Degraded, degraded.Status);
        }
    }

    [Fact]
    public void Unavailable_never_carries_component_records()
    {
        ModuleCatalogHealth unavailable =
            ModuleCatalogHealth.Unavailable(ModulesRoot, nameof(UnauthorizedAccessException));

        Assert.Equal(ModuleInventoryStatus.Unavailable, unavailable.Status);
        Assert.Empty(unavailable.Components);
        Assert.Equal(nameof(UnauthorizedAccessException), unavailable.FailureCategory);
    }

    [Fact]
    public void MainViewModel_maps_each_inventory_status_to_the_table_defined_notice()
    {
        I18n i18n = TestI18n.Full("en");

        Assert.Equal(
            string.Empty,
            MainViewModelFor(i18n, ModuleCatalogHealth.FromComponents(ModulesRoot, []))
                .ModuleHealthNotice);
        Assert.Equal(
            string.Empty,
            MainViewModelFor(
                    i18n,
                    ModuleCatalogHealth.FromComponents(
                        ModulesRoot,
                        [new("clean", ModuleComponentStatus.Loaded, null)]))
                .ModuleHealthNotice);
        Assert.Equal(
            i18n["modules.notice.degraded"],
            MainViewModelFor(
                    i18n,
                    ModuleCatalogHealth.FromComponents(
                        ModulesRoot,
                        [new("clean", ModuleComponentStatus.Malformed, nameof(BadImageFormatException))]))
                .ModuleHealthNotice);
        Assert.Equal(
            i18n["modules.notice.unavailable"],
            MainViewModelFor(
                    i18n,
                    ModuleCatalogHealth.Unavailable(ModulesRoot, nameof(UnauthorizedAccessException)))
                .ModuleHealthNotice);
    }

    [Fact]
    public void SettingsViewModel_projects_every_component_status_with_distinct_table_text()
    {
        I18n i18n = TestI18n.Full("en");
        ModuleCatalogHealth health = ModuleCatalogHealth.FromComponents(
            ModulesRoot,
            [
                new("loaded", ModuleComponentStatus.Loaded, null),
                new("incomplete", ModuleComponentStatus.Incomplete, null),
                new("malformed", ModuleComponentStatus.Malformed, nameof(BadImageFormatException)),
                new("unreadable", ModuleComponentStatus.Unreadable, nameof(IOException)),
            ]);
        SettingsViewModel vm = SettingsViewModelFor(i18n, health);

        Assert.Collection(
            vm.ModuleComponents,
            loaded =>
            {
                Assert.Equal("loaded", loaded.Name);
                Assert.Equal(i18n["modules.status.loaded"], loaded.StatusText);
                Assert.Equal(string.Empty, loaded.ReasonText);
            },
            incomplete =>
            {
                Assert.Equal("incomplete", incomplete.Name);
                Assert.Equal(i18n["modules.status.incomplete"], incomplete.StatusText);
                Assert.Equal(i18n["modules.reason.incomplete"], incomplete.ReasonText);
            },
            malformed =>
            {
                Assert.Equal("malformed", malformed.Name);
                Assert.Equal(i18n["modules.status.malformed"], malformed.StatusText);
                Assert.Equal(
                    i18n.Format("modules.reason.malformed", nameof(BadImageFormatException)),
                    malformed.ReasonText);
            },
            unreadable =>
            {
                Assert.Equal("unreadable", unreadable.Name);
                Assert.Equal(i18n["modules.status.unreadable"], unreadable.StatusText);
                Assert.Equal(
                    i18n.Format("modules.reason.unreadable", nameof(IOException)),
                    unreadable.ReasonText);
            });
    }

    [Fact]
    public void ModulesEmptyNote_is_nonempty_only_when_inventory_is_not_installed()
    {
        I18n i18n = TestI18n.Full("en");
        SettingsViewModel notInstalled = SettingsViewModelFor(
            i18n,
            ModuleCatalogHealth.FromComponents(ModulesRoot, []));
        SettingsViewModel complete = SettingsViewModelFor(
            i18n,
            ModuleCatalogHealth.FromComponents(
                ModulesRoot,
                [new("clean", ModuleComponentStatus.Loaded, null)]));
        SettingsViewModel degraded = SettingsViewModelFor(
            i18n,
            ModuleCatalogHealth.FromComponents(
                ModulesRoot,
                [new("clean", ModuleComponentStatus.Incomplete, null)]));
        SettingsViewModel unavailable = SettingsViewModelFor(
            i18n,
            ModuleCatalogHealth.Unavailable(ModulesRoot, nameof(UnauthorizedAccessException)));

        Assert.Equal(i18n["modules.none"], notInstalled.ModulesEmptyNote);
        Assert.Equal(string.Empty, complete.ModulesEmptyNote);
        Assert.Equal(string.Empty, degraded.ModulesEmptyNote);
        Assert.Equal(string.Empty, unavailable.ModulesEmptyNote);
        Assert.Equal(
            i18n.Format("modules.inventory.unavailable", nameof(UnauthorizedAccessException)),
            unavailable.ModulesInventoryNote);
        Assert.Equal(string.Empty, notInstalled.ModulesInventoryNote);
        Assert.Equal(string.Empty, complete.ModulesInventoryNote);
        Assert.Equal(string.Empty, degraded.ModulesInventoryNote);
    }

    [Fact]
    public void Module_health_notice_follows_a_runtime_language_switch()
    {
        I18n shellI18n = TestI18n.Full("en");
        ModuleCatalogHealth degraded = ModuleCatalogHealth.FromComponents(
            ModulesRoot,
            [new("broken", ModuleComponentStatus.Malformed, nameof(BadImageFormatException))]);
        MainViewModel shell = MainViewModelFor(shellI18n, degraded);
        var shellRaised = new List<string?>();
        shell.PropertyChanged += (_, e) => shellRaised.Add(e.PropertyName);

        Assert.Equal(shellI18n["modules.notice.degraded"], shell.ModuleHealthNotice);
        shellI18n.SelectedCulture = "tr";
        Assert.Equal(shellI18n["modules.notice.degraded"], shell.ModuleHealthNotice);
        Assert.Contains(nameof(MainViewModel.ModuleHealthNotice), shellRaised);

        I18n settingsI18n = TestI18n.Full("en");
        SettingsViewModel settings = SettingsViewModelFor(settingsI18n, degraded);
        var settingsRaised = new List<string?>();
        settings.PropertyChanged += (_, e) => settingsRaised.Add(e.PropertyName);

        ModuleComponentRow english = Assert.Single(settings.ModuleComponents);
        Assert.Equal(settingsI18n["modules.status.malformed"], english.StatusText);
        Assert.Equal(
            settingsI18n.Format("modules.reason.malformed", nameof(BadImageFormatException)),
            english.ReasonText);

        settingsI18n.SelectedCulture = "tr";

        ModuleComponentRow turkish = Assert.Single(settings.ModuleComponents);
        Assert.Equal(settingsI18n["modules.status.malformed"], turkish.StatusText);
        Assert.Equal(
            settingsI18n.Format("modules.reason.malformed", nameof(BadImageFormatException)),
            turkish.ReasonText);
        Assert.Contains(nameof(SettingsViewModel.ModuleComponents), settingsRaised);
    }

    [Fact]
    public void Composition_root_registers_module_catalog_health()
    {
        var services = new ServiceCollection();
        WpfApp.ConfigureServices(services, []);
        using ServiceProvider provider = services.BuildServiceProvider();

        var registered = provider.GetRequiredService<ModuleCatalogHealth>();
        Assert.NotNull(registered);

        // NotNull on the registration alone leaves the two GetRequiredService reads that CONSUME it
        // unobserved: substituting a fabricated health at either call site restores the original defect
        // end-to-end (shell silent, Settings claiming "no components installed") with the suite green.
        // Every other health test builds its ViewModel by hand, so this is the only place the wire itself
        // can be seen. Assert both ends carry the REGISTERED instance's values, not merely some instance.
        var settings = provider.GetRequiredService<SettingsViewModel>();
        Assert.Equal(registered.ModulesRoot, settings.ModulesRoot);
        Assert.Equal(registered.Components.Count, settings.ModuleComponents.Count);

        var main = provider.GetRequiredService<MainViewModel>();
        Assert.Equal(
            MainViewModelFor(provider.GetRequiredService<I18n>(), registered).ModuleHealthNotice,
            main.ModuleHealthNotice);
    }

    private static MainViewModel MainViewModelFor(I18n i18n, ModuleCatalogHealth health)
        => new(i18n, Array.Empty<IWckModule>(), health);

    private static SettingsViewModel SettingsViewModelFor(I18n i18n, ModuleCatalogHealth health)
        => new(i18n, new FakeThemeService(), new RecordingUrlOpener(), health);

    private sealed class FakeThemeService : IThemeService
    {
        public IReadOnlyList<AppTheme> AvailableThemes { get; } = [AppTheme.Dark, AppTheme.Light];
        public AppTheme SelectedTheme { get; private set; } = AppTheme.Dark;
        public AppTheme AppliedTheme => AppTheme.Dark;
        public bool RestartRequired => SelectedTheme != AppliedTheme;

        public bool TrySelectTheme(AppTheme theme)
        {
            SelectedTheme = theme;
            return true;
        }
    }
}

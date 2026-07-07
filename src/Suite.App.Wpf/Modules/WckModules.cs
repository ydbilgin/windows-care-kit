using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Win32;

namespace WindowsCareKit.App.Modules;

public sealed class UninstallModule : IWckModule
{
    public string Id => "uninstall";
    public string TitleKey => "nav.uninstall";
    public string DescKey => "nav.uninstall.desc";
    public string IconKey => "\uE74D";
    public int Order => 10;
    public bool IsSettings => false;

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<ILeftoverProbe, Win32LeftoverProbe>();
        services.AddSingleton<IAppxRemover>(sp => new Win32AppxRemover(sp.GetRequiredService<ExecutionLog>()));
        services.AddSingleton<UninstallViewModel>();
    }

    public object CreateContent(IServiceProvider sp) => sp.GetRequiredService<UninstallViewModel>();

    public FrameworkElement? CreateView() => null;
}

public sealed class MigrationModule : IWckModule
{
    public string Id => "migration";
    public string TitleKey => "nav.migration";
    public string DescKey => "nav.migration.desc";
    public string IconKey => "\uE7AD";
    public int Order => 40;
    public bool IsSettings => false;

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IMsiCatalog, Win32MsiCatalog>();
        services.AddSingleton<IStartMenuShortcutReader, Win32StartMenuShortcutReader>();
        services.AddSingleton<IContentSignatureProbe>(_ => new Win32ContentSignatureProbe());
        services.AddSingleton<IRecipeFileSystem, Win32RecipeFileSystem>();
        services.AddSingleton<IProgramSource>(sp => new RegistryUninstallSource(
            sp.GetRequiredService<IInstalledAppReader>(), new Win32PathCanonicalizer()));
        services.AddSingleton<IProgramSource>(sp => new MsiProductSource(
            new Win32MsiCatalog(),
            new Win32PathCanonicalizer(),
            sp.GetRequiredService<ICurrentSidProvider>().GetCurrentSid()));
        services.AddSingleton<IProgramSource>(sp => new AppxProgramSource(
            sp.GetRequiredService<IAppxReader>(), new Win32PathCanonicalizer()));
        services.AddSingleton<IProgramSource>(sp => new AppPathsSource(
            sp.GetRequiredService<IRegistryProbe>(), new Win32PathCanonicalizer()));
        services.AddSingleton<IProgramSource>(sp => new StartMenuSource(
            sp.GetRequiredService<IStartMenuShortcutReader>(), new Win32PathCanonicalizer()));
        services.AddSingleton<IMigrationScanService>(sp => new MigrationScanService(
            sp.GetServices<IProgramSource>(),
            ProfileRoots.ForCurrentUser,
            sp.GetRequiredService<IRecipeFileSystem>(),
            sp.GetRequiredService<IContentSignatureProbe>()));
        services.AddSingleton<Func<IReadOnlyList<MigrationRecipe>>>(_ => BuiltinRecipeSource.LoadAll);
        services.AddSingleton(sp => new RecipeResolver(
            new RecipePathResolver(ProfileRoots.ForCurrentUser()),
            sp.GetRequiredService<IRecipeFileSystem>()));
        services.AddSingleton<MigrationInstallManifestStore>();
        services.AddSingleton<MigrationBackupRunner>();
        services.AddSingleton<IMigrationBackupRunner>(sp => sp.GetRequiredService<MigrationBackupRunner>());
        services.AddSingleton<MigrationViewModel>();
    }

    public object CreateContent(IServiceProvider sp) => sp.GetRequiredService<MigrationViewModel>();

    public FrameworkElement? CreateView() => null;
}

public sealed class SettingsModule : IWckModule
{
    public string Id => "settings";
    public string TitleKey => "nav.settings";
    public string DescKey => "nav.settings.desc";
    public string IconKey => "\uE713";
    public int Order => 900;
    public bool IsSettings => true;

    public void RegisterServices(IServiceCollection services)
    {
    }

    public object CreateContent(IServiceProvider sp) => sp.GetRequiredService<SettingsViewModel>();

    public FrameworkElement? CreateView() => null;
}

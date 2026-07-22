using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.App.Views;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Win32;

namespace WindowsCareKit.App.Modules;

public sealed class MigrationModule : IWckModule
{
    public string Id => "migration";
    public string TitleKey => "nav.migration";
    public string DescKey => "nav.migration.desc";
    public string IconKey => "\uE7AD";
    public int Order => ModuleOrder.Migration;
    public bool IsSettings => false;

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IMsiCatalog, Win32MsiCatalog>();
        services.AddSingleton<IStartMenuShortcutReader, Win32StartMenuShortcutReader>();
        services.AddSingleton<IContentSignatureProbe>(_ => new Win32ContentSignatureProbe());
        services.AddSingleton<IRecipeFileSystem, Win32RecipeFileSystem>();
        services.AddSingleton<IProgramSource>(sp => new RegistryUninstallSource(
            sp.GetRequiredService<IInstalledAppReader>(),
            sp.GetRequiredService<IPathCanonicalizer>()));
        services.AddSingleton<IProgramSource>(sp => new MsiProductSource(
            sp.GetRequiredService<IMsiCatalog>(),
            sp.GetRequiredService<IPathCanonicalizer>(),
            sp.GetRequiredService<ICurrentSidProvider>().GetCurrentSid()));
        services.AddSingleton<IProgramSource>(sp => new AppxProgramSource(
            sp.GetRequiredService<IAppxReader>(),
            sp.GetRequiredService<IPathCanonicalizer>()));
        services.AddSingleton<IProgramSource>(sp => new AppPathsSource(
            sp.GetRequiredService<IRegistryProbe>(),
            sp.GetRequiredService<IPathCanonicalizer>()));
        services.AddSingleton<IProgramSource>(sp => new StartMenuSource(
            sp.GetRequiredService<IStartMenuShortcutReader>(),
            sp.GetRequiredService<IPathCanonicalizer>()));
        services.AddSingleton<Func<IReadOnlyList<MigrationRecipe>>>(_ => BuiltinRecipeSource.LoadAll);
        services.AddSingleton<IMigrationScanService>(sp => new MigrationScanService(
            sp.GetServices<IProgramSource>(),
            ProfileRoots.ForCurrentUser,
            sp.GetRequiredService<IRecipeFileSystem>(),
            sp.GetRequiredService<IContentSignatureProbe>(),
            sp.GetRequiredService<Func<IReadOnlyList<MigrationRecipe>>>()));
        services.AddSingleton(sp => new RecipeResolver(
            new RecipePathResolver(ProfileRoots.ForCurrentUser()),
            sp.GetRequiredService<IRecipeFileSystem>()));
        services.AddSingleton<MigrationInstallManifestStore>();
        services.AddSingleton<MigrationPackageMarkerStore>();
        services.AddSingleton<MigrationBackupRunner>();
        services.AddSingleton<IMigrationBackupRunner>(sp => sp.GetRequiredService<MigrationBackupRunner>());
        services.AddSingleton<MigrationViewModel>();
    }

    public object CreateContent(IServiceProvider sp) => sp.GetRequiredService<MigrationViewModel>();

    public FrameworkElement? CreateView() => new MigrationView();
}

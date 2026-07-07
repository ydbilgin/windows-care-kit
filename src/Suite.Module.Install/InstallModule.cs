using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.App.Views;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Win32;

namespace WindowsCareKit.App.Modules;

public sealed class InstallModule : IWckModule
{
    public string Id => "install";
    public string TitleKey => "nav.install";
    public string DescKey => "nav.install.desc";
    public string IconKey => "\uE896";
    public int Order => 60;
    public bool IsSettings => false;

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IInstallManifestLoader, InstallManifestLoader>();
        services.AddSingleton<IAuthProbe, Win32AuthProbe>();
        services.AddSingleton<IDriverGuard, Win32DriverGuard>();
        services.AddSingleton<IInstallPlanWriter, InstallPlanWriter>();
        services.AddSingleton(sp => new InstallRunner(
            sp.GetRequiredService<IInstallPlanWriter>(), sp.GetRequiredService<IClock>()));
        services.AddSingleton<InstallViewModel>();
    }

    public object CreateContent(IServiceProvider sp) => sp.GetRequiredService<InstallViewModel>();

    public FrameworkElement? CreateView() => new InstallView();
}

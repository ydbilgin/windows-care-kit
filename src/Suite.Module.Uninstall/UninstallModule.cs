using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Modules;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Module.Uninstall.ViewModels;
using WindowsCareKit.Module.Uninstall.Views;
using WindowsCareKit.Win32;

namespace WindowsCareKit.Module.Uninstall;

public sealed class UninstallModule : IWckModule
{
    public string Id => "uninstall";
    public string TitleKey => "nav.uninstall";
    public string DescKey => "nav.uninstall.desc";
    public string IconKey => "\uE74D";
    public int Order => ModuleOrder.Uninstall;
    public bool IsSettings => false;

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<ILeftoverProbe, Win32LeftoverProbe>();
        services.AddSingleton<UninstallViewModel>();
    }

    public object CreateContent(IServiceProvider sp) => sp.GetRequiredService<UninstallViewModel>();

    public FrameworkElement? CreateView() => new UninstallView();
}

using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Modules;
using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Module.Clean.ViewModels;
using WindowsCareKit.Module.Clean.Views;
using WindowsCareKit.Win32;

namespace WindowsCareKit.Module.Clean;

public sealed class CleanModule : IWckModule
{
    public string Id => "clean";
    public string TitleKey => "nav.clean";
    public string DescKey => "nav.clean.desc";
    public string IconKey => "\uE75C";
    public int Order => ModuleOrder.Clean;
    public bool IsSettings => false;

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IJunkProbe, Win32JunkProbe>();
        services.AddSingleton<IStartupProbe, Win32StartupProbe>();
        services.AddSingleton<IBrowserExtensionInventory, Win32BrowserExtensionInventory>();
        services.AddSingleton<IRecycleBinService, Win32RecycleBinService>();
        services.AddSingleton<CleanViewModel>();
    }

    public object CreateContent(IServiceProvider sp) => sp.GetRequiredService<CleanViewModel>();

    public FrameworkElement? CreateView() => new CleanView();
}

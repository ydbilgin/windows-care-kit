using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.App.Views;
using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Win32;

namespace WindowsCareKit.App.Modules;

public sealed class CleanModule : IWckModule
{
    public string Id => "clean";
    public string TitleKey => "nav.clean";
    public string DescKey => "nav.clean.desc";
    public string IconKey => "\uE75C";
    public int Order => 20;
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

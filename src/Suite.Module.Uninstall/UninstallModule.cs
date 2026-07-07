using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.App.Views;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Uninstall;
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

    public FrameworkElement? CreateView() => new UninstallView();
}

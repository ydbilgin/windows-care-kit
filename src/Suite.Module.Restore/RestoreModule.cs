using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Modules;
using WindowsCareKit.Module.Restore.ViewModels;
using WindowsCareKit.Module.Restore.Views;

namespace WindowsCareKit.Module.Restore;

public sealed class RestoreModule : IWckModule
{
    public string Id => "restore";
    public string TitleKey => "nav.restore";
    public string DescKey => "nav.restore.desc";
    public string IconKey => "\uE81C";
    public int Order => ModuleOrder.Restore;
    public bool IsSettings => false;

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<RestoreViewModel>();
    }

    public object CreateContent(IServiceProvider sp) => sp.GetRequiredService<RestoreViewModel>();

    public FrameworkElement? CreateView() => new RestoreView();
}

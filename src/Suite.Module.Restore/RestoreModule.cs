using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.App.Views;

namespace WindowsCareKit.App.Modules;

public sealed class RestoreModule : IWckModule
{
    public string Id => "restore";
    public string TitleKey => "nav.restore";
    public string DescKey => "nav.restore.desc";
    public string IconKey => "\uE81C";
    public int Order => 50;
    public bool IsSettings => false;

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<RestoreViewModel>();
    }

    public object CreateContent(IServiceProvider sp) => sp.GetRequiredService<RestoreViewModel>();

    public FrameworkElement? CreateView() => new RestoreView();
}

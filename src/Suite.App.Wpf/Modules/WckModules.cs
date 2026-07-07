using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.ViewModels;

namespace WindowsCareKit.App.Modules;

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

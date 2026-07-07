using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsCareKit.App.Modules;

public interface IWckModule
{
    string Id { get; }
    string TitleKey { get; }
    string DescKey { get; }
    string IconKey { get; }
    int Order { get; }
    bool IsSettings { get; }
    void RegisterServices(IServiceCollection services);
    object CreateContent(IServiceProvider sp);
    FrameworkElement? CreateView();
}

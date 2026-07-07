using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.App.Views;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Win32;

namespace WindowsCareKit.App.Modules;

public sealed class BackupModule : IWckModule
{
    public string Id => "backup";
    public string TitleKey => "nav.backup";
    public string DescKey => "nav.backup.desc";
    public string IconKey => "\uE74E";
    public int Order => 30;
    public bool IsSettings => false;

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IEnvironmentExpander, Win32EnvironmentExpander>();
        services.AddSingleton<IManifestLoader, ManifestLoader>();
        services.AddSingleton<BackupPlanner>();
        services.AddSingleton<BackupReportWriter>();
        services.AddSingleton<IIntegrityWriter, BackupIntegrityWriter>();
        services.AddSingleton<BackupRunner>();
        services.AddSingleton<BackupViewModel>();
    }

    public object CreateContent(IServiceProvider sp) => sp.GetRequiredService<BackupViewModel>();

    public FrameworkElement? CreateView() => new BackupView();
}

using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Localization;

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

    /// <summary>Module-owned i18n fragment for <paramref name="culture"/>. Default reads the embedded
    /// lang.&lt;culture&gt;.json (LogicalName-pinned) from the module's own assembly; empty when absent.</summary>
    IReadOnlyDictionary<string, string> GetLangFragment(string culture)
        => LangFragments.ReadEmbedded(GetType().Assembly, culture);
}

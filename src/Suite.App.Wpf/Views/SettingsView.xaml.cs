using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Execution.Adapters;

namespace WindowsCareKit.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void ExternalLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        IUrlOpener opener = Application.Current is App app
            ? app.Services.GetRequiredService<IUrlOpener>()
            : new UrlOpener();
        opener.Open(e.Uri);
        e.Handled = true;
    }
}

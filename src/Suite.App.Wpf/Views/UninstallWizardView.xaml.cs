using System.Windows.Controls;
using WindowsCareKit.App.ViewModels;

namespace WindowsCareKit.App.Views;

/// <summary>
/// The 4-beat uninstall wizard overlay (PR-4). Pure view; all state lives in the bound
/// <see cref="UninstallWizardViewModel"/>. The only code-behind concern is keeping the selection-derived
/// state (the "Seçilenleri Sil" enablement + selection summary) in step when a per-row checkbox toggles —
/// the node's <c>IsChecked</c> is bound two-way, but the aggregate enablement is computed on the VM and is
/// cheapest to refresh from the checkbox event.
/// </summary>
public partial class UninstallWizardView : UserControl
{
    public UninstallWizardView()
    {
        InitializeComponent();
    }

    private void OnNodeCheckChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is UninstallWizardViewModel vm)
            vm.RaiseSelectionState();
    }
}

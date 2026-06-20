using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WindowsCareKit.App.ViewModels;

namespace WindowsCareKit.App.Controls;

/// <summary>
/// Reusable confirmation gate (UI decision §B2). Pure view; all state lives in the bound
/// <see cref="ConfirmGateViewModel"/>. The only code-behind concern is focus: on the IRREVERSIBLE tier
/// the spec requires <b>Cancel</b> to be the default-focused button (so a reflexive Enter cancels, not
/// approves) — that can't be expressed in XAML bindings alone.
/// </summary>
public partial class ConfirmGate : UserControl
{
    public ConfirmGate()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ConfirmGateViewModel oldVm)
            oldVm.PropertyChanged -= OnGatePropertyChanged;
        if (e.NewValue is ConfirmGateViewModel newVm)
            newVm.PropertyChanged += OnGatePropertyChanged;
    }

    private void OnGatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ConfirmGateViewModel vm)
            return;
        if (e.PropertyName != nameof(ConfirmGateViewModel.IsOpen) || !vm.IsOpen)
            return;

        // Defer until the just-opened gate has rendered, then focus Cancel on the irreversible tier so the
        // non-destructive default has focus (spec §B2). Lower tiers leave focus alone.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (vm.IsIrreversibleTier)
                CancelButton.Focus();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }
}

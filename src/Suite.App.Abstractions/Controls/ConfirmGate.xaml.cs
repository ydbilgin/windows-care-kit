using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindowsCareKit.App.ViewModels;

namespace WindowsCareKit.App.Controls;

/// <summary>
/// Reusable confirmation gate (UI decision §B2). Pure view; all state lives in the bound
/// <see cref="ConfirmGateViewModel"/>. Two concerns genuinely belong here because neither can be expressed
/// as a binding:
/// <list type="number">
/// <item><b>Focus.</b> On the IRREVERSIBLE tier <b>Cancel</b> is the default-focused button, so a reflexive
/// Enter cancels rather than approves.</item>
/// <item><b>What the fold hides.</b> Whether a row is clipped is a fact about laid-out geometry, so only the
/// view can measure it. It measures, then reports the counts to the view-model, which owns the localized
/// sentence (SPEC §1.3 / A-M3-4).</item>
/// </list>
/// </summary>
public partial class ConfirmGate : UserControl
{
    /// <summary>Sub-pixel slack. A row whose bottom edge lands within half a pixel of the viewport's is
    /// fully readable; treating that rounding as "clipped" would make the affordance cry wolf.</summary>
    private const double EdgeTolerance = 0.5;

    public ConfirmGate()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        GateScroll.ScrollChanged += (_, _) => ReportClipping();
        GateRowsHost.LayoutUpdated += (_, _) => ReportClipping();
    }

    /// <summary>
    /// Counts the rows the viewport is not showing in full, and how many of those state they will not run,
    /// then hands both numbers to the view-model. It counts ROW CONTAINERS rather than reading
    /// <c>ScrollableHeight</c> alone, because "there is more below" and "a refusal is below" are different
    /// facts and §1.3 exists for the second one.
    /// </summary>
    private void ReportClipping()
    {
        if (DataContext is not ConfirmGateViewModel vm)
            return;

        // A closed gate has no fold to describe, and the whole control is collapsed, so every container
        // measures zero. Reporting from there would answer a question nobody asked, once per layout pass.
        if (!vm.IsOpen)
        {
            vm.ReportClippedRows(0, 0);
            return;
        }

        double viewport = GateScroll.ViewportHeight;
        if (viewport <= 0)
        {
            vm.ReportClippedRows(0, 0);
            return;
        }

        int hidden = 0;
        int hiddenBlocked = 0;
        for (int index = 0; index < GateRowsHost.Items.Count; index++)
        {
            if (GateRowsHost.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
                continue;
            if (!container.IsArrangeValid || container.RenderSize.Height <= 0)
                continue;

            Rect bounds = container
                .TransformToAncestor(GateScroll)
                .TransformBounds(new Rect(new Point(0, 0), container.RenderSize));

            bool fullyVisible = bounds.Top >= -EdgeTolerance && bounds.Bottom <= viewport + EdgeTolerance;
            if (fullyVisible)
                continue;

            hidden++;
            if (GateRowsHost.Items[index] is PlanRow { IsSkipped: true })
                hiddenBlocked++;
        }

        vm.ReportClippedRows(hidden, hiddenBlocked);
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

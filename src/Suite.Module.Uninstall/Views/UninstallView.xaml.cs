using System.Windows;
using System.Windows.Controls;

namespace WindowsCareKit.App.Views;

public partial class UninstallView : UserControl
{
    // Below this content width the secondary columns (Yayıncı / Boyut / Sürüm) collapse so the grid stays
    // readable in a narrow window (UI decision §2: "Dar pencerede Yayıncı/Boyut/Sürüm gizlenir").
    private const double NarrowThreshold = 880;

    public UninstallView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        Loaded += (_, _) => ApplyResponsiveColumns(ActualWidth);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyResponsiveColumns(e.NewSize.Width);

    private void ApplyResponsiveColumns(double width)
    {
        Visibility v = width < NarrowThreshold ? Visibility.Collapsed : Visibility.Visible;
        if (PublisherColumn is not null) PublisherColumn.Visibility = v;
        if (SizeColumn is not null) SizeColumn.Visibility = v;
        if (VersionColumn is not null) VersionColumn.Visibility = v;
    }
}

using System.Windows;
using System.Windows.Controls;

namespace WindowsCareKit.App.Controls;

/// <summary>
/// A per-source health note: "partial data (reason)" beside the section it qualifies.
/// <para>
/// The family is fixed to <see cref="ChipFamily.Attention"/> and is not settable. Partial data is attention,
/// not irreversibility — red is reserved for irreversible / blocked / failed (decision §2.5-12), and a health
/// note that can be talked into the red family by a call site is how that reservation erodes.
/// </para>
/// </summary>
public partial class HealthChip : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(HealthChip),
        new PropertyMetadata(string.Empty));

    public HealthChip() => InitializeComponent();

    /// <summary>The already-localized note, including its reason. Supplied by the caller's view model, so the
    /// note re-renders on a language switch and this control owns no i18n key.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The family this note always renders in. Exposed read-only so a test can assert the guarantee
    /// rather than trusting the XAML literal.</summary>
    public static ChipFamily Family => ChipFamily.Attention;
}

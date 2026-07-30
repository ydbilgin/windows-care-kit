using System.Windows;
using System.Windows.Controls;

namespace WindowsCareKit.App.Controls;

/// <summary>
/// The shared chip primitive: a pill that renders one Fluent glyph plus one text label in one
/// <see cref="ChipFamily"/>'s theme tokens. It decides nothing — the family, glyph and text are all supplied
/// by the wrapper control, which is what keeps the vocabulary policy in
/// <see cref="RiskChipFamilies"/> and the pixels here.
/// </summary>
public partial class FamilyChip : UserControl
{
    public static readonly DependencyProperty FamilyProperty = DependencyProperty.Register(
        nameof(Family), typeof(ChipFamily), typeof(FamilyChip),
        new PropertyMetadata(ChipFamily.Neutral));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(FamilyChip),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FamilyChip),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping), typeof(TextWrapping), typeof(FamilyChip),
        new PropertyMetadata(System.Windows.TextWrapping.NoWrap));

    public FamilyChip() => InitializeComponent();

    /// <summary>Which of the four visual families this chip belongs to.</summary>
    public ChipFamily Family
    {
        get => (ChipFamily)GetValue(FamilyProperty);
        set => SetValue(FamilyProperty, value);
    }

    /// <summary>A Segoe Fluent Icons codepoint. Required: a chip must never state meaning by colour alone.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>The chip's label. Supplied already localized by the caller — this control owns no i18n key,
    /// so a language switch is the caller's binding, not a string frozen in the component layer.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>How the label behaves when it does not fit. Defaults to <see cref="System.Windows.TextWrapping.NoWrap"/>
    /// for one-word grade chips; a host rendering a whole sentence (HealthChip) asks for
    /// <see cref="System.Windows.TextWrapping.Wrap"/>, because ellipsizing an honesty note would hide content
    /// with no other route to it.</summary>
    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }
}

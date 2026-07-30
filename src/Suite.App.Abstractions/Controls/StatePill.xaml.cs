using System.Windows;
using System.Windows.Controls;

namespace WindowsCareKit.App.Controls;

/// <summary>
/// The screen's machine-state pill: PREVIEW / RUNNING / DONE.
/// <para>
/// It is neutral-info in every state and has no family property to set. Emerald states a capability or marks
/// the primary action; using it for "DONE" would make a machine state read as a verdict about the user's
/// PC — the exact conflation the redesign exists to remove (decision §2.4 emerald scarcity, §5.5).
/// </para>
/// </summary>
public partial class StatePill : UserControl
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(StatePillState), typeof(StatePill),
        new PropertyMetadata(StatePillState.Preview, OnStateChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatePill),
        new PropertyMetadata(string.Empty));

    private static readonly DependencyPropertyKey GlyphPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Glyph), typeof(string), typeof(StatePill),
        new PropertyMetadata(ChipGlyphs.Preview));

    public static readonly DependencyProperty GlyphProperty = GlyphPropertyKey.DependencyProperty;

    public StatePill() => InitializeComponent();

    /// <summary>Which machine state the screen is in.</summary>
    public StatePillState State
    {
        get => (StatePillState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>The already-localized label the caller supplies for the current state.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The derived Fluent glyph, so the three states are distinguishable without colour — the pill
    /// is one family in all three, so the glyph and the text are the whole signal.</summary>
    public string Glyph => (string)GetValue(GlyphProperty);

    /// <summary>The family this pill always renders in. Exposed read-only so a test can assert the "never
    /// emerald" guarantee rather than trusting the XAML literal.</summary>
    public static ChipFamily Family => ChipFamily.Neutral;

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StatePill)d).SetValue(GlyphPropertyKey, GlyphFor((StatePillState)e.NewValue));

    private static string GlyphFor(StatePillState state) => state switch
    {
        StatePillState.Running => ChipGlyphs.Running,
        StatePillState.Done => ChipGlyphs.Done,
        _ => ChipGlyphs.Preview,
    };
}

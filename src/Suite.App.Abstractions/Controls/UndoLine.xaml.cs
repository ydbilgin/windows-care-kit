using System.Windows;
using System.Windows.Controls;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.App.Controls;

/// <summary>
/// The per-row recovery statement. Give it the row's TYPED <see cref="UndoCapability"/> and the already
/// rendered undo sentence; it derives the ink family and glyph.
/// <para>
/// It reads <see cref="Recovery"/>, never the text. The rendered sentence is localized and must not be
/// parsed — the recovery family has to be invariant under a language switch, which it cannot be if the
/// colour is inferred from words (SPEC §1.2).
/// </para>
/// </summary>
public partial class UndoLine : UserControl
{
    public static readonly DependencyProperty RecoveryProperty = DependencyProperty.Register(
        nameof(Recovery), typeof(UndoCapability), typeof(UndoLine),
        new PropertyMetadata(UndoCapability.None, OnRecoveryChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(UndoLine),
        new PropertyMetadata(string.Empty));

    private static readonly DependencyPropertyKey FamilyPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Family), typeof(ChipFamily), typeof(UndoLine),
        new PropertyMetadata(ChipFamily.Neutral));

    private static readonly DependencyPropertyKey GlyphPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Glyph), typeof(string), typeof(UndoLine),
        new PropertyMetadata(ChipGlyphs.Locked));

    public static readonly DependencyProperty FamilyProperty = FamilyPropertyKey.DependencyProperty;
    public static readonly DependencyProperty GlyphProperty = GlyphPropertyKey.DependencyProperty;

    public UndoLine() => InitializeComponent();

    /// <summary>The row's typed recovery capability — the ONLY input that decides how this line reads.
    /// Defaults to <see cref="UndoCapability.None"/>: an unset line promises nothing.</summary>
    public UndoCapability Recovery
    {
        get => (UndoCapability)GetValue(RecoveryProperty);
        set => SetValue(RecoveryProperty, value);
    }

    /// <summary>The already-localized recovery sentence, typically <c>PlanRow.Undo</c>. The view layer must
    /// not compose a mechanism string here: absent an engine mechanism field, only the generic
    /// Full/Partial/None wording is permitted (SPEC §4, decision §4.4).</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The derived ink family. <see cref="UndoCapability.None"/> maps to
    /// <see cref="ChipFamily.Neutral"/> — muted, not red — because the row's RiskChip already states the
    /// irreversibility and a second alarm on the same row is noise, not honesty.</summary>
    public ChipFamily Family => (ChipFamily)GetValue(FamilyProperty);

    /// <summary>The derived Fluent glyph, so recovery state survives a colour-blind or High Contrast read.</summary>
    public string Glyph => (string)GetValue(GlyphProperty);

    private static void OnRecoveryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var recovery = (UndoCapability)e.NewValue;
        d.SetValue(FamilyPropertyKey, RiskChipFamilies.For(recovery));
        d.SetValue(GlyphPropertyKey, GlyphFor(recovery));
    }

    private static string GlyphFor(UndoCapability recovery) => recovery switch
    {
        UndoCapability.Full => ChipGlyphs.Undo,
        UndoCapability.Partial => ChipGlyphs.Warning,
        _ => ChipGlyphs.Locked,
    };
}

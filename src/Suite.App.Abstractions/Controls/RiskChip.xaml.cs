using System.Windows;
using System.Windows.Controls;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.App.Controls;

/// <summary>
/// The risk chip for a plan row, tier banner or section header. Give it the engine's
/// <see cref="RiskLevel"/>, whether the row is skipped, and the already-localized grade word; it derives the
/// visual family and glyph itself.
/// <para>
/// It carries no colour of its own. The family comes from <see cref="RiskChipFamilies"/> and the brushes come
/// from theme resources at render time, which is what makes it theme-following — the defect the frozen
/// per-risk palette had by construction.
/// </para>
/// </summary>
public partial class RiskChip : UserControl
{
    public static readonly DependencyProperty RiskProperty = DependencyProperty.Register(
        nameof(Risk), typeof(RiskLevel), typeof(RiskChip),
        new PropertyMetadata(RiskLevel.Info, OnVocabularyChanged));

    public static readonly DependencyProperty IsBlockedProperty = DependencyProperty.Register(
        nameof(IsBlocked), typeof(bool), typeof(RiskChip),
        new PropertyMetadata(false, OnVocabularyChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(RiskChip),
        new PropertyMetadata(string.Empty));

    private static readonly DependencyPropertyKey FamilyPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Family), typeof(ChipFamily), typeof(RiskChip),
        new PropertyMetadata(ChipFamily.Neutral));

    private static readonly DependencyPropertyKey GlyphPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Glyph), typeof(string), typeof(RiskChip),
        new PropertyMetadata(ChipGlyphs.Info));

    public static readonly DependencyProperty FamilyProperty = FamilyPropertyKey.DependencyProperty;
    public static readonly DependencyProperty GlyphProperty = GlyphPropertyKey.DependencyProperty;

    public RiskChip() => InitializeComponent();

    /// <summary>The engine's risk level for the action this chip describes.</summary>
    public RiskLevel Risk
    {
        get => (RiskLevel)GetValue(RiskProperty);
        set => SetValue(RiskProperty, value);
    }

    /// <summary>True when the row will NOT run. Overrides <see cref="Risk"/> for the family: a blocked row is
    /// red whatever the underlying action's risk was.</summary>
    public bool IsBlocked
    {
        get => (bool)GetValue(IsBlockedProperty);
        set => SetValue(IsBlockedProperty, value);
    }

    /// <summary>The exact grade word, already localized by the caller — typically <c>PlanRow.RiskText</c>,
    /// which resolves <c>plan.risk.*</c> / <c>plan.blocked</c> and re-renders on a language switch. This
    /// control owns no i18n key, so no lang table changes when the chip does.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The derived visual family. Read-only: it is a function of <see cref="Risk"/> and
    /// <see cref="IsBlocked"/>, and a caller that could set it independently could put a Critical action in
    /// the calm family.</summary>
    public ChipFamily Family => (ChipFamily)GetValue(FamilyProperty);

    /// <summary>The derived Fluent glyph, so the chip states its meaning without relying on colour.</summary>
    public string Glyph => (string)GetValue(GlyphProperty);

    private static void OnVocabularyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RiskChip)d).RefreshVocabulary();

    private void RefreshVocabulary()
    {
        ChipFamily family = RiskChipFamilies.For(Risk, IsBlocked);
        SetValue(FamilyPropertyKey, family);
        SetValue(GlyphPropertyKey, GlyphFor(family));
    }

    private static string GlyphFor(ChipFamily family) => family switch
    {
        ChipFamily.Reversible => ChipGlyphs.Undo,
        ChipFamily.Attention => ChipGlyphs.Warning,
        ChipFamily.Irreversible => ChipGlyphs.Error,
        _ => ChipGlyphs.Info,
    };
}

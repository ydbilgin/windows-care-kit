using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WindowsCareKit.App.Controls;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Tests.TestInfra;
using Xunit;
using Xunit.Abstractions;

namespace WindowsCareKit.Tests;

/// <summary>
/// Render coverage for the M1 shared component layer. The vocabulary FUNCTION — which risk maps to which
/// family — is asserted without WPF in <see cref="ChipVocabularyTests"/>; what these add is the different and
/// stronger claim: that the mapping reaches the SCREEN.
/// <para>
/// Every colour assertion in this file is read off a rendered visual tree. None of them consults a
/// hand-written family-to-token table, because such a table can agree perfectly with the theme while the
/// pixels disagree with both: WPF ranks a style setter above an inherited value, so an implicit
/// <c>TextBlock</c> style in the theme silently outranked the chip's inherited family ink and every family
/// rendered the generic body colour. That defect was arithmetically invisible to a token-table assertion and
/// visible in one line of a render.
/// </para>
/// </summary>
[Collection(WpfResourceCollection.Name)]
public sealed class ChipControlRenderTests(ITestOutputHelper output)
{
    /// <summary>WCAG AA for normal-size text. The chip label is 10-11.5pt, so the large-text 3:1 exemption
    /// never applies to it.</summary>
    private const double MinimumContrastRatio = 4.5;

    /// <summary>A row cell narrow enough that a one-sentence honesty note cannot fit on one line. cx measured
    /// the pre-fix control rendering such a note at 845x15px inside a host this wide.</summary>
    private const double NarrowHostWidth = 220;

    private const double WideHostWidth = 420;

    private const string LongHealthNote =
        "Partial data (HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Run: SecurityException) — the key "
        + "could not be read, so this list may be incomplete.";

    /// <summary>
    /// A5, the machine-checkable half: no chip may state its meaning by colour alone. For every risk level,
    /// every skipped row, every pill state and every recovery value, the rendered control must carry both a
    /// non-empty Fluent glyph and non-empty text — which is what keeps it readable in High Contrast, where
    /// the wash and border can be overridden away entirely.
    /// </summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void Every_chip_state_renders_a_glyph_and_text(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool created = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                foreach (RiskLevel risk in Enum.GetValues<RiskLevel>())
                {
                    AssertGlyphAndText(new RiskChip { Risk = risk, Text = risk.ToString() }, $"RiskChip({risk})");
                    AssertGlyphAndText(
                        new RiskChip { Risk = risk, IsBlocked = true, Text = "BLOCKED" },
                        $"RiskChip({risk}, blocked)");
                }

                foreach (StatePillState state in Enum.GetValues<StatePillState>())
                    AssertGlyphAndText(new StatePill { State = state, Text = state.ToString() }, $"StatePill({state})");

                foreach (UndoCapability recovery in Enum.GetValues<UndoCapability>())
                {
                    AssertGlyphAndText(
                        new UndoLine { Recovery = recovery, Text = "undo: " + recovery },
                        $"UndoLine({recovery})");
                }

                AssertGlyphAndText(
                    new HealthChip { Text = "Partial data (HKLM Run: SecurityException)" },
                    "HealthChip");
            }
            finally
            {
                CleanupApplicationResources(created, theme);
            }
        });
    }

    // ---- §3.1 the family ink must reach the pixels -----------------------------------------------------------

    /// <summary>
    /// The ink a family publishes must arrive at the glyph AND the label. <c>ChipRoot</c>'s style is the ONE
    /// place the family-to-token mapping lives, so this compares the two rendered <c>TextBlock</c>s against
    /// what that single source published — no second table, in this test or anywhere else.
    /// <para>
    /// This is the assertion the whole round exists for. Both shipped themes merge an implicit
    /// <c>&lt;Style TargetType="TextBlock"&gt;</c> that sets <c>Foreground</c>, and a style setter outranks an
    /// inherited value in WPF's precedence order — so publishing the family ink as an inherited
    /// <c>TextElement.Foreground</c> on the root Border left all four families rendering the generic body ink
    /// while every token-table assertion stayed green. The ink has to arrive as a LOCAL value.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void Every_family_ink_reaches_the_rendered_glyph_and_label(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool created = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                foreach (ChipFamily family in Enum.GetValues<ChipFamily>())
                {
                    FamilyChip chip = RenderFamilyChip(family);
                    Color published = InkOf(ChipRootOf(chip));

                    AssertInkArrived(themeName, family.ToString(), published, Named<TextBlock>(chip, "ChipText"));
                    AssertInkArrived(themeName, family.ToString(), published, Named<TextBlock>(chip, "ChipGlyph"));
                }
            }
            finally
            {
                CleanupApplicationResources(created, theme);
            }
        });
    }

    /// <summary>The recovery line has the identical shape — an inherited ink published by the panel that
    /// carries the triggers — so it needs the identical proof. Swept over every
    /// <see cref="UndoCapability"/> rather than one convenient value.</summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void Every_recovery_ink_reaches_the_rendered_undo_line(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool created = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                foreach (UndoCapability recovery in Enum.GetValues<UndoCapability>())
                {
                    var line = new UndoLine { Recovery = recovery, Text = "undo: " + recovery };
                    Render(line);
                    Color published = InkOf(Named<StackPanel>(line, "UndoRoot"));

                    AssertInkArrived(themeName, $"undo:{recovery}", published, Named<TextBlock>(line, "UndoText"));
                    AssertInkArrived(themeName, $"undo:{recovery}", published, Named<TextBlock>(line, "UndoGlyph"));
                }
            }
            finally
            {
                CleanupApplicationResources(created, theme);
            }
        });
    }

    // ---- §3.2 AA, derived from the render ---------------------------------------------------------------------

    /// <summary>
    /// Every family in every shipped theme clears WCAG AA — measured from the ink the label ACTUALLY rendered
    /// over the wash the root ACTUALLY rendered. The predecessor of this test resolved a hand-written token
    /// pair and computed a perfectly correct ratio for two brushes, one of which never reached a pixel.
    /// <para>The wash may be a gradient, so the ratio is taken against its WORST stop: a label is legible only
    /// if it is legible everywhere it is drawn.</para>
    /// </summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void Every_chip_family_meets_AA_contrast_in_the_pixels_it_renders(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool created = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                foreach (ChipFamily family in Enum.GetValues<ChipFamily>())
                {
                    FamilyChip chip = RenderFamilyChip(family);
                    Color ink = InkOf(Named<TextBlock>(chip, "ChipText"));
                    Color[] wash = WashOf(chip);
                    double ratio = wash.Min(stop => Contrast.Ratio(Hex(ink), Hex(stop)));

                    output.WriteLine(
                        $"AA  {themeName,-9} {family,-13} ink {Hex(ink)} on wash "
                        + $"{string.Join('/', wash.Select(Hex))} = {ratio:F2}:1");

                    Assert.True(
                        ratio >= MinimumContrastRatio,
                        $"{themeName} {family} rendered {ratio:F2}:1 (ink {Hex(ink)} on wash "
                        + $"{string.Join('/', wash.Select(Hex))}), below the WCAG AA "
                        + $"{MinimumContrastRatio:F1}:1 floor for normal-size text.");
                }
            }
            finally
            {
                CleanupApplicationResources(created, theme);
            }
        });
    }

    /// <summary>
    /// The families must be told apart on screen, so no two of them may render the same ink or the same wash.
    /// <para>This is also the whole-enum guard: a family added to <see cref="ChipFamily"/> without a trigger in
    /// <c>FamilyChip.xaml</c> falls through to the default (Neutral) setters and renders Neutral's pixels, so
    /// it collides here and is named. An unmapped family therefore cannot pass quietly — which is the property
    /// the retired token-table sweep was worth keeping.</para>
    /// </summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void No_two_chip_families_render_the_same_pixels(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool created = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                var rendered = new List<(ChipFamily Family, string Ink, string Wash)>();
                foreach (ChipFamily family in Enum.GetValues<ChipFamily>())
                {
                    FamilyChip chip = RenderFamilyChip(family);
                    rendered.Add((
                        family,
                        Hex(InkOf(Named<TextBlock>(chip, "ChipText"))),
                        string.Join('/', WashOf(chip).Select(Hex))));
                }

                foreach ((ChipFamily Family, string Ink, string Wash) left in rendered)
                {
                    foreach ((ChipFamily Family, string Ink, string Wash) right in rendered)
                    {
                        if (left.Family >= right.Family)
                            continue;

                        Assert.True(
                            left.Ink != right.Ink && left.Wash != right.Wash,
                            $"{themeName}: {left.Family} and {right.Family} render the same pixels "
                            + $"(ink {left.Ink}/{right.Ink}, wash {left.Wash}/{right.Wash}). Either a family "
                            + "was added to ChipFamily without a mapping in FamilyChip.xaml — it then falls "
                            + "through to the Neutral default — or two families share a token pair, and the "
                            + "reader cannot tell them apart.");
                    }
                }
            }
            finally
            {
                CleanupApplicationResources(created, theme);
            }
        });
    }

    /// <summary>
    /// The point of the whole control: the chip's brushes come from the theme dictionary at render time, so
    /// the SAME family resolves to different pixels in Daylight and Strongbox. A chip that had kept a frozen
    /// palette would render identically in both and pass every other test in this file.
    /// </summary>
    [Fact]
    public void A_chip_resolves_different_brushes_in_each_theme()
    {
        Color[] daylight = RenderedChipBackground("Daylight", RiskLevel.Medium);
        Color[] strongbox = RenderedChipBackground("Strongbox", RiskLevel.Medium);

        Assert.NotEqual(daylight, strongbox);
    }

    /// <summary>The pill is neutral-info in all three states. Emerald states a capability or marks the primary
    /// action; a DONE pill in emerald would read as a verdict about the user's PC.
    /// <para>Asserted against the pixels a Neutral and a Reversible <see cref="FamilyChip"/> actually render,
    /// not against a token key restated in the test — the assertion has to fail when the pill turns emerald on
    /// screen, whatever the table says.</para></summary>
    [Theory]
    [InlineData(StatePillState.Preview)]
    [InlineData(StatePillState.Running)]
    [InlineData(StatePillState.Done)]
    public void StatePill_is_never_emerald(StatePillState state)
    {
        RunOnStaThread(() =>
        {
            bool created = EnsureApplicationResources("Strongbox", out ResourceDictionary theme);
            try
            {
                var pill = new StatePill { State = state, Text = state.ToString() };
                Render(pill);

                Assert.Equal(ChipFamily.Neutral, StatePill.Family);
                Assert.Equal(WashOf(RenderFamilyChip(ChipFamily.Neutral)), WashOf(pill));
                Assert.NotEqual(WashOf(RenderFamilyChip(ChipFamily.Reversible)), WashOf(pill));
            }
            finally
            {
                CleanupApplicationResources(created, theme);
            }
        });
    }

    /// <summary>The three pill states must be distinguishable without colour, since all three share one
    /// family — so the glyph is the whole non-textual signal and no two may collide.</summary>
    [Fact]
    public void StatePill_uses_a_distinct_glyph_for_each_state()
    {
        RunOnStaThread(() =>
        {
            string[] glyphs = Enum.GetValues<StatePillState>()
                .Select(state => new StatePill { State = state }.Glyph)
                .ToArray();

            Assert.All(glyphs, glyph => Assert.False(string.IsNullOrEmpty(glyph)));
            Assert.Equal(glyphs.Length, glyphs.Distinct(StringComparer.Ordinal).Count());
        });
    }

    /// <summary>The chip derives its family from the engine risk through the single mapping, so re-pointing
    /// that mapping (for example Medium to emerald) turns this red at the control boundary and not only in the
    /// pure-vocabulary test.</summary>
    [Theory]
    [InlineData(RiskLevel.Info, ChipFamily.Neutral)]
    [InlineData(RiskLevel.Low, ChipFamily.Reversible)]
    [InlineData(RiskLevel.Medium, ChipFamily.Attention)]
    [InlineData(RiskLevel.High, ChipFamily.Attention)]
    [InlineData(RiskLevel.Critical, ChipFamily.Irreversible)]
    public void RiskChip_derives_its_family_from_the_engine_risk(RiskLevel risk, ChipFamily expected)
    {
        RunOnStaThread(() =>
        {
            var chip = new RiskChip { Risk = risk, Text = risk.ToString() };

            Assert.Equal(expected, chip.Family);
            Assert.Equal(ChipFamily.Irreversible, new RiskChip { Risk = risk, IsBlocked = true }.Family);
        });
    }

    /// <summary>A health note is amber by construction. There is no call site that can talk it into red,
    /// which is what protects red's reservation for irreversible / blocked / failed. Compared against a
    /// rendered Attention chip and against a rendered Irreversible one, so the claim is about pixels.</summary>
    [Fact]
    public void HealthChip_is_always_the_attention_family()
    {
        RunOnStaThread(() =>
        {
            bool created = EnsureApplicationResources("Daylight", out ResourceDictionary theme);
            try
            {
                var chip = new HealthChip { Text = "Partial data (Edge/Default: UnauthorizedAccessException)" };
                Render(chip);

                Assert.Equal(ChipFamily.Attention, HealthChip.Family);
                Assert.Equal(WashOf(RenderFamilyChip(ChipFamily.Attention)), WashOf(chip));
                Assert.NotEqual(WashOf(RenderFamilyChip(ChipFamily.Irreversible)), WashOf(chip));
            }
            finally
            {
                CleanupApplicationResources(created, theme);
            }
        });
    }

    /// <summary>The recovery line reads the TYPED capability, never the rendered sentence. Proven by feeding
    /// it a sentence that contradicts the typed value: if the control were parsing text, the emerald-sounding
    /// wording would win — it must not.</summary>
    [Fact]
    public void UndoLine_reads_the_typed_recovery_not_the_rendered_sentence()
    {
        RunOnStaThread(() =>
        {
            var line = new UndoLine
            {
                Recovery = UndoCapability.None,
                Text = "undo: restores from Recycle Bin",
            };

            Assert.Equal(ChipFamily.Neutral, line.Family);
            Assert.Equal(ChipGlyphs.Locked, line.Glyph);

            var full = new UndoLine { Recovery = UndoCapability.Full, Text = "undo: none — permanent" };
            Assert.Equal(ChipFamily.Reversible, full.Family);
            Assert.Equal(ChipGlyphs.Undo, full.Glyph);
        });
    }

    // ---- §3.5 a health note must be able to wrap ---------------------------------------------------------------

    /// <summary>
    /// Six live honesty notes render through <see cref="HealthChip"/>, and a note that runs off the edge of
    /// its cell is content hidden with no other route to it. The chip's inner panel therefore has to hand the
    /// label a real width constraint; a horizontal <c>StackPanel</c> measures its children at INFINITE width,
    /// so <c>TextWrapping="Wrap"</c> could never fire and cx measured a note at 845x15px inside a 220px host.
    /// <para>
    /// The line box is derived from the label's own resolved font metrics rather than from a constant this
    /// test also owns, so the assertion cannot validate its own arithmetic.
    /// </para>
    /// </summary>
    [Fact]
    public void A_long_health_note_wraps_inside_a_narrow_host()
    {
        RunOnStaThread(() =>
        {
            bool created = EnsureApplicationResources("Strongbox", out ResourceDictionary theme);
            try
            {
                var note = new HealthChip { Text = LongHealthNote };
                Render(note, NarrowHostWidth);
                TextBlock label = Named<TextBlock>(note, "ChipText");
                double lineBox = LineBoxOf(label);

                Assert.True(
                    label.ActualHeight >= lineBox * 2,
                    $"A {LongHealthNote.Length}-character honesty note rendered "
                    + $"{label.ActualWidth:F0}x{label.ActualHeight:F0}px inside a {NarrowHostWidth:F0}px host — "
                    + $"{label.ActualHeight / lineBox:F1} line(s) against a {lineBox:F1}px line box. It must "
                    + "wrap: a note that runs off the edge is content with no other route to it.");

                Assert.True(
                    label.ActualWidth <= NarrowHostWidth,
                    $"The note's label rendered {label.ActualWidth:F0}px wide inside a "
                    + $"{NarrowHostWidth:F0}px host, so it draws outside its cell.");
            }
            finally
            {
                CleanupApplicationResources(created, theme);
            }
        });
    }

    /// <summary>
    /// The other half of §2.4: the grade pill is a one-word chip sized by its own content and must NOT be
    /// stretched by the label column. Stated as an invariance — the same pill in a 220px and a 420px host
    /// renders the same width — so it cannot be satisfied by a magic threshold that happens to hold today.
    /// </summary>
    [Fact]
    public void A_grade_pill_stays_one_line_and_does_not_grow_with_its_host()
    {
        RunOnStaThread(() =>
        {
            bool created = EnsureApplicationResources("Strongbox", out ResourceDictionary theme);
            try
            {
                var narrow = new RiskChip { Risk = RiskLevel.Critical, Text = "CRITICAL" };
                var wide = new RiskChip { Risk = RiskLevel.Critical, Text = "CRITICAL" };
                Render(narrow, NarrowHostWidth);
                Render(wide, WideHostWidth);

                double narrowWidth = ChipRootOf(narrow).ActualWidth;
                double wideWidth = ChipRootOf(wide).ActualWidth;

                Assert.True(
                    Math.Abs(narrowWidth - wideWidth) < 0.5,
                    $"A CRITICAL pill rendered {narrowWidth:F1}px in a {NarrowHostWidth:F0}px host and "
                    + $"{wideWidth:F1}px in a {WideHostWidth:F0}px host. A grade pill is sized by its word; "
                    + "growing with the cell means the label column is stretching it.");

                Assert.True(
                    narrowWidth < NarrowHostWidth,
                    $"A CRITICAL pill filled {narrowWidth:F1}px of its {NarrowHostWidth:F0}px host.");

                TextBlock label = Named<TextBlock>(narrow, "ChipText");
                Assert.True(
                    label.ActualHeight < LineBoxOf(label) * 2,
                    $"A CRITICAL pill's label rendered {label.ActualHeight:F1}px against a "
                    + $"{LineBoxOf(label):F1}px line box, so the one-word grade chip has wrapped.");
            }
            finally
            {
                CleanupApplicationResources(created, theme);
            }
        });
    }

    // ---- harness ------------------------------------------------------------------------------------------------

    private static Color[] RenderedChipBackground(string themeName, RiskLevel risk)
    {
        Color[] background = [];
        RunOnStaThread(() =>
        {
            bool created = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                var chip = new RiskChip { Risk = risk, Text = risk.ToString() };
                Render(chip);
                background = WashOf(chip);
            }
            finally
            {
                CleanupApplicationResources(created, theme);
            }
        });
        return background;
    }

    private static void AssertInkArrived(string themeName, string label, Color published, TextBlock rendered)
    {
        Color arrived = InkOf(rendered);
        Assert.True(
            published == arrived,
            $"{themeName} {label}: the root publishes ink {Hex(published)} but {rendered.Name} renders "
            + $"{Hex(arrived)}. WPF ranks a style setter above an inherited value, so the theme's implicit "
            + "TextBlock style wins unless the family ink arrives at the TextBlock as a LOCAL value.");
    }

    private static FamilyChip RenderFamilyChip(ChipFamily family)
    {
        var chip = new FamilyChip { Family = family, Glyph = ChipGlyphs.Info, Text = family.ToString() };
        Render(chip);
        return chip;
    }

    /// <summary>The chip's root Border.
    /// <para>Located by the <c>ChipRoot</c> name rather than by "the first Border in the tree": a
    /// <see cref="UserControl"/>'s own default template contributes a transparent chrome Border of its own, so
    /// a positional lookup silently reads the wrapper's null Background and would report every chip as
    /// unstyled — a green-looking test asserting nothing about the chip.</para></summary>
    private static Border ChipRootOf(FrameworkElement chip) => Named<Border>(chip, "ChipRoot");

    /// <summary>The wash the chip ACTUALLY rendered with, as the set of colours it is painted from. A gradient
    /// wash contributes every stop, because a label has to stay legible over all of them.</summary>
    private static Color[] WashOf(FrameworkElement chip) => WashColors(ChipRootOf(chip).Background);

    private static Color[] WashColors(Brush brush) => brush switch
    {
        SolidColorBrush solid => [solid.Color],
        GradientBrush gradient => gradient.GradientStops.Select(stop => stop.Color).Distinct().ToArray(),
        _ => throw new InvalidOperationException(
            $"A chip wash must be a solid or gradient brush to be measurable; got {brush?.GetType().Name ?? "null"}."),
    };

    /// <summary>The ink in force on an element. <c>TextBlock.Foreground</c> and the attached
    /// <c>TextElement.Foreground</c> the chip root publishes are the SAME dependency property, so one reader
    /// serves both ends of the comparison and neither side can be read through a different lens.</summary>
    private static Color InkOf(DependencyObject element)
        => Assert.IsType<SolidColorBrush>(element.GetValue(TextElement.ForegroundProperty)).Color;

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>One line of text in the label's OWN resolved typography — style-derived, so the wrap
    /// assertion cannot be satisfied by a constant the test also chose.</summary>
    private static double LineBoxOf(TextBlock label) => label.FontFamily.LineSpacing * label.FontSize;

    private static void AssertGlyphAndText(FrameworkElement chip, string label)
    {
        Render(chip);

        TextBlock[] blocks = Descendants<TextBlock>(chip)
            .Where(block => !string.IsNullOrEmpty(block.Text))
            .ToArray();

        Assert.True(blocks.Length >= 2, $"{label} rendered {blocks.Length} non-empty text runs; a chip must " +
            "carry BOTH a glyph and a label so it never states meaning by colour alone.");
        Assert.Contains(blocks, block => block.FontFamily.Source.Contains("Fluent", StringComparison.Ordinal)
                                      || block.FontFamily.Source.Contains("MDL2", StringComparison.Ordinal));
        Assert.True(chip.ActualWidth > 0 && chip.ActualHeight > 0, $"{label} rendered with zero size.");
    }

    /// <summary>Renders into a host that STRETCHES its content, which is how every real call site places a
    /// chip — inside a Grid cell. A host that left-aligned its content would hand the chip an unconstrained
    /// arrangement and no wrap assertion made against it would mean anything.</summary>
    private static void Render(FrameworkElement element, double width = WideHostWidth, double height = 200)
    {
        var host = new ContentControl
        {
            Content = element,
            Width = width,
            Height = height,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var size = new Size(width, height);
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.UpdateLayout();
    }

    private static bool EnsureApplicationResources(string themeName, out ResourceDictionary theme)
    {
        bool createdApplication = Application.Current is null;
        Application application = Application.Current
            ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        theme = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/WindowsCareKit;component/Themes/{themeName}.xaml",
                UriKind.Absolute),
        };
        application.Resources.MergedDictionaries.Add(theme);
        return createdApplication;
    }

    private static void CleanupApplicationResources(bool createdApplication, ResourceDictionary theme)
    {
        Application.Current?.Resources.MergedDictionaries.Remove(theme);
        _ = createdApplication;
    }

    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
        => Assert.Single(Descendants<T>(root), element => element.Name == name);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
                yield return typed;
            foreach (T descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

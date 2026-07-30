using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindowsCareKit.App.Controls;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Render coverage for the M1 shared component layer. The vocabulary itself is asserted without WPF in
/// <see cref="ChipVocabularyTests"/>; what these add is the part only a real render can prove — that each
/// control resolves its brushes from the THEME (not a frozen literal), that it actually measures and arranges
/// in both themes, and that every state puts a glyph AND text on screen rather than relying on colour.
/// </summary>
[Collection(WpfResourceCollection.Name)]
public sealed class ChipControlRenderTests
{
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

    /// <summary>
    /// The point of the whole control: the chip's brushes come from the theme dictionary at render time, so
    /// the SAME family resolves to different pixels in Daylight and Strongbox. A chip that had kept a frozen
    /// palette would render identically in both and pass every other test in this file.
    /// </summary>
    [Fact]
    public void A_chip_resolves_different_brushes_in_each_theme()
    {
        Color daylight = RenderedChipBackground("Daylight", RiskLevel.Medium);
        Color strongbox = RenderedChipBackground("Strongbox", RiskLevel.Medium);

        Assert.NotEqual(daylight, strongbox);
    }

    /// <summary>The pill is neutral-info in all three states. Emerald states a capability or marks the primary
    /// action; a DONE pill in emerald would read as a verdict about the user's PC.</summary>
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
                Assert.Equal(ChipBackground("Strongbox", ChipFamily.Neutral), RenderedWash(pill));
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

    /// <summary>MP-4's target. The chip derives its family from the engine risk through the single mapping,
    /// so re-pointing that mapping (for example Medium to emerald) turns this red at the control boundary and
    /// not only in the pure-vocabulary test.</summary>
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
    /// which is what protects red's reservation for irreversible / blocked / failed.</summary>
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
                Assert.Equal(ChipBackground("Daylight", ChipFamily.Attention), RenderedWash(chip));
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

    private static Color RenderedChipBackground(string themeName, RiskLevel risk)
    {
        Color background = default;
        RunOnStaThread(() =>
        {
            bool created = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                var chip = new RiskChip { Risk = risk, Text = risk.ToString() };
                Render(chip);
                background = RenderedWash(chip);
            }
            finally
            {
                CleanupApplicationResources(created, theme);
            }
        });
        return background;
    }

    /// <summary>The wash the chip ACTUALLY rendered with.
    /// <para>Located by the <c>ChipRoot</c> name rather than by "the first Border in the tree": a
    /// <see cref="UserControl"/>'s own default template contributes a transparent chrome Border of its own, so
    /// a positional lookup silently reads the wrapper's null Background and would report every chip as
    /// unstyled — a green-looking test asserting nothing about the chip.</para></summary>
    private static Color RenderedWash(FrameworkElement chip)
    {
        Border root = Assert.Single(Descendants<Border>(chip), border => border.Name == "ChipRoot");
        return Assert.IsType<SolidColorBrush>(root.Background).Color;
    }

    /// <summary>The colour the theme dictionary says a family's wash is — read from the dictionary, not from
    /// the control, so the assertion compares the render against an independent source.</summary>
    private static Color ChipBackground(string themeName, ChipFamily family)
    {
        var dictionary = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/WindowsCareKit;component/Themes/{themeName}.xaml",
                UriKind.Absolute),
        };

        string key = family switch
        {
            ChipFamily.Reversible => "Backup.OkWash",
            ChipFamily.Attention => "Wck.Attention.Wash",
            ChipFamily.Irreversible => "Backup.Row.Danger",
            _ => "Wck.Info.Wash",
        };

        return ((SolidColorBrush)dictionary[key]).Color;
    }

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

    private static void Render(FrameworkElement element)
    {
        var host = new ContentControl { Content = element, Width = 420, Height = 120 };
        var size = new Size(420, 120);
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

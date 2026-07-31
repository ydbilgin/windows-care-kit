using System.Xml.Linq;
using WindowsCareKit.App.Controls;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// The chip vocabulary contract (SPEC §3 M1, decision §2.1): colour carries the FAMILY, text carries the
/// exact grade word. These are the assertions that make the mapping a contract rather than a XAML detail —
/// they need no rendering, so they stay fast and they fail on the mapping itself rather than on pixels.
/// </summary>
public sealed class ChipVocabularyTests
{
    /// <summary>The binding family table. Medium and High deliberately share the AMBER family while keeping
    /// distinct grade words: the family says "this needs attention", the text says which grade — collapsing
    /// them into one word, or splitting them into two colours, both break the decision's rule that colour
    /// carries family and text carries grade.</summary>
    [Theory]
    [InlineData(RiskLevel.Info, ChipFamily.Neutral)]
    [InlineData(RiskLevel.Low, ChipFamily.Reversible)]
    [InlineData(RiskLevel.Medium, ChipFamily.Attention)]
    [InlineData(RiskLevel.High, ChipFamily.Attention)]
    [InlineData(RiskLevel.Critical, ChipFamily.Irreversible)]
    public void Risk_level_maps_to_the_binding_chip_family(RiskLevel risk, ChipFamily expected)
        => Assert.Equal(expected, RiskChipFamilies.For(risk, isSkipped: false));

    /// <summary>A row the engine will NOT run is BLOCKED, and blocked is red whatever the underlying action's
    /// risk was. Reading the risk level of a skipped row would paint the most safety-critical row in the calm
    /// colour of the work it is refusing — asserted across every level, not just the convenient one.</summary>
    [Theory]
    [InlineData(RiskLevel.Info)]
    [InlineData(RiskLevel.Low)]
    [InlineData(RiskLevel.Medium)]
    [InlineData(RiskLevel.High)]
    [InlineData(RiskLevel.Critical)]
    public void A_skipped_row_is_the_blocked_family_whatever_its_risk(RiskLevel risk)
        => Assert.Equal(ChipFamily.Irreversible, RiskChipFamilies.For(risk, isSkipped: true));

    /// <summary>Emerald states a capability and must never caption safety, so exactly ONE risk level may
    /// resolve to it — the reversible one. Stated as a whole-enum sweep so adding a level cannot quietly
    /// widen emerald's reach.</summary>
    [Fact]
    public void Only_the_reversible_level_is_ever_emerald()
    {
        RiskLevel[] emerald = Enum.GetValues<RiskLevel>()
            .Where(level => RiskChipFamilies.For(level, isSkipped: false) == ChipFamily.Reversible)
            .ToArray();

        Assert.Equal([RiskLevel.Low], emerald);
    }

    /// <summary>Recovery families. <see cref="UndoCapability.None"/> maps to Neutral (muted ink), NOT to the
    /// red family: decision §2.1 requires "undo: none — permanent" to be stated calmly, because the row's own
    /// RiskChip already carries the irreversibility and a second alarm is noise, not honesty.</summary>
    [Theory]
    [InlineData(UndoCapability.Full, ChipFamily.Reversible)]
    [InlineData(UndoCapability.Partial, ChipFamily.Attention)]
    [InlineData(UndoCapability.None, ChipFamily.Neutral)]
    public void Recovery_maps_to_its_ink_family(UndoCapability recovery, ChipFamily expected)
        => Assert.Equal(expected, RiskChipFamilies.For(recovery));

    /// <summary>Every family resolves to its shipped theme foreground and wash with WCAG AA text contrast.
    /// The whole-enum sweep makes a newly added family fail until its token pair joins the vocabulary.</summary>
    [Fact]
    public void Every_chip_family_meets_AA_contrast_in_both_themes()
    {
        const double minimumRatio = 4.5;

        foreach (string themeName in new[] { "Strongbox", "Daylight" })
        {
            foreach (ChipFamily family in Enum.GetValues<ChipFamily>())
            {
                var (foregroundKey, washKey) = TokensFor(family);
                string foreground = Assert.Single(ThemeColors(themeName, foregroundKey));
                string[] washes = ThemeColors(themeName, washKey);
                double ratio = washes.Min(wash => Contrast.Ratio(foreground, wash));

                Assert.True(
                    ratio >= minimumRatio,
                    $"{themeName} {family} chip measured {ratio:F2}:1 ({foregroundKey} {foreground} on "
                    + $"{washKey} {string.Join('/', washes)}), below the WCAG AA {minimumRatio:F1}:1 floor.");
            }
        }
    }

    /// <summary>
    /// A1 / the mono sweep. Cascadia Code's ligatures visually rewrite literal text — <c>-&gt;</c> renders as
    /// an arrow — and this app's whole promise is the literal path, glyph for glyph. No source file may ask
    /// for it; the one authoritative family is the <c>Wck.Mono</c> token.
    /// </summary>
    [Fact]
    public void No_source_file_asks_for_the_ligature_carrying_mono_font()
    {
        string[] offenders = SourceFiles()
            .Where(path => File.ReadAllText(path).Contains("Cascadia Code", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Cascadia Code must not appear in src/; use {{DynamicResource Wck.Mono}}. Found in: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// A2 / the chip-ink sweep. No production source carries the retired chip-ink literal; every chip resolves
    /// its foreground from its family's theme token.
    /// </summary>
    [Fact]
    public void No_view_hardcodes_the_chip_ink()
    {
        const string retiredChipInk = "#" + "181410";
        string[] offenders = SourceFiles()
            .Where(path => File.ReadAllText(path).Contains(retiredChipInk, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "The retired chip-ink literal must not appear in src/. Found in: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// A4. A partial-data note is ATTENTION, not irreversibility — red is reserved for
    /// irreversible / blocked / failed (decision §2.5-12). Asserted over the view sources so a note that is
    /// re-reddened by hand fails here, at the site, rather than only in a render test somebody may not add.
    /// <para><c>MainWindow.xaml</c> is excluded: its <c>ModuleHealthNotice</c> is owned by a concurrent
    /// change and is converted in the follow-up, so listing it here as an allowed exception keeps the debt
    /// visible instead of silently widening the guard to "views except whichever ones fail".</para>
    /// </summary>
    [Theory]
    [InlineData("CleanView.xaml")]
    [InlineData("BackupView.xaml")]
    [InlineData("InstallView.xaml")]
    [InlineData("UninstallView.xaml")]
    public void No_health_note_renders_in_the_danger_family(string viewFileName)
    {
        string path = SourceFiles().Single(file => Path.GetFileName(file) == viewFileName);
        string[] offenders = File.ReadAllLines(path)
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(entry => entry.Line.Contains("HealthNote", StringComparison.Ordinal)
                         || entry.Line.Contains("InventoryNotice", StringComparison.Ordinal))
            .Where(entry => entry.Line.Contains("Danger", StringComparison.Ordinal))
            .Select(entry => $"{viewFileName}:{entry.Number}")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A partial-data note must render in the amber attention family, never red. Found at: "
            + string.Join(", ", offenders));
    }

    private static (string Foreground, string Wash) TokensFor(ChipFamily family) => family switch
    {
        ChipFamily.Neutral => ("Wck.Info.Fg", "Wck.Info.Wash"),
        ChipFamily.Reversible => ("Backup.OkFg", "Backup.OkWash"),
        ChipFamily.Attention => ("Wck.Attention.Fg", "Wck.Attention.Wash"),
        ChipFamily.Irreversible => ("Backup.DangerStrong", "Backup.Row.Danger"),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "A chip family needs a theme token pair."),
    };

    private static string[] ThemeColors(string themeName, string key)
    {
        string path = Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "Themes", themeName + ".xaml");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement resource = XDocument.Load(path).Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Key") == key);

        return resource.DescendantsAndSelf()
            .Select(element => (string?)element.Attribute("Color"))
            .Where(color => color is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Every hand-written source file under <c>src/</c>. Build output is excluded because it is a
    /// copy of the source, not an independent site — a guard that counted it would fail on stale
    /// <c>bin</c>/<c>obj</c> content and get "fixed" by loosening the pattern.</summary>
    private static IEnumerable<string> SourceFiles()
        => Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string RepoRoot
    {
        get
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WindowsCareKit.slnx")))
                dir = dir.Parent;

            return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
        }
    }
}

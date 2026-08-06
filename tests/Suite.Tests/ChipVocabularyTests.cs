using System.Xml;
using System.Xml.Linq;
using WindowsCareKit.App.Controls;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// The chip vocabulary contract (SPEC §3 M1, decision §2.1): colour carries the FAMILY, text carries the
/// exact grade word. These are the assertions that make the mapping a contract rather than a XAML detail —
/// they need no rendering, so they stay fast and they fail on the mapping itself rather than on pixels.
/// <para>
/// They assert the mapping FUNCTION and the SOURCES that call it, and they are honest that this is a
/// different claim from "the mapping reaches the screen". That second claim is only provable from a rendered
/// visual tree and lives in <see cref="ChipControlRenderTests"/>. No family-to-token table is written down
/// here any more: one used to be, it agreed with the theme perfectly, and every chip on screen was rendering
/// a colour neither of them named.
/// </para>
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

    /// <summary>
    /// A3 / the wiring sweep. Every <c>RiskChip</c> in a shipped view must state BOTH halves of the fact it
    /// renders: the engine's risk AND whether the engine will refuse to run the row. A site that binds only
    /// <c>Risk</c> paints a blocked row in the calm colour of the work it is refusing, and nothing else
    /// catches it — both cross-family reviewers deleted one <c>IsBlocked</c> binding and the whole suite
    /// stayed green.
    /// <para>
    /// The elements are ENUMERATED from the XAML rather than counted, because a count survives a site being
    /// added half-wired while another is deleted. Parsing is real XML parsing, not a regex, so a prefix other
    /// than <c>controls:</c> and an attribute order other than the one in use today are both handled.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_risk_chip_site_binds_both_the_risk_and_the_blocked_fact()
    {
        (string Site, XElement Element)[] sites = RiskChipSites().ToArray();

        // Non-vacuity: an enumeration that finds nothing would pass every assertion below for the wrong
        // reason. This is not the count the guard asserts on — it only proves the sweep reached the views.
        Assert.True(
            sites.Length > 0,
            "The RiskChip sweep found no <RiskChip> element under src/. The guard is not reading the views.");

        string[] halfWired = sites
            .Select(site => (
                site.Site,
                Missing: RequiredRiskChipAttributes
                    .Where(attribute => site.Element.Attribute(attribute) is null)
                    .ToArray()))
            .Where(site => site.Missing.Length > 0)
            .Select(site => $"{site.Site} (missing {string.Join(" and ", site.Missing)})")
            .ToArray();

        Assert.True(
            halfWired.Length == 0,
            "Every RiskChip must bind both the engine's risk and its blockedness; blocked DOMINATES the risk "
            + "level, so a site that omits one renders a row the engine refuses in the colour of the work it "
            + "is refusing. Half-wired sites: " + string.Join(", ", halfWired));
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

    /// <summary>
    /// B2 / the colour-lever sweep. A display-only row states blockedness through its own
    /// <c>PlanRow.Disposition</c>; it must never reach for <see cref="RiskLevel.Critical"/> to obtain the red
    /// family. That is the two-facts-in-one-value defect <c>a0d2c52</c> removed, and it re-enters silently:
    /// six factories carried it, every render looked right, and the row was lying about what the engine said.
    /// <para>
    /// Asserted over the SOURCES of the six view-models that build display-only rows, as an exact set rather
    /// than a loosened pattern — each surviving <c>RiskLevel.Critical</c> statement is named with the reason it
    /// is honest. A seventh statement, or a changed one, fails here at the site rather than only in a render
    /// test somebody may not add.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(DisplayRowViewModels))]
    public void No_display_only_view_model_uses_the_critical_risk_level_as_a_colour_lever(string viewModelFileName)
    {
        string path = SourceFiles().Single(file => Path.GetFileName(file) == viewModelFileName);
        (string Text, int Number)[] found = File.ReadAllLines(path)
            .Select((line, index) => (Text: line.Trim(), Number: index + 1))
            .Where(entry => entry.Text.Contains("RiskLevel.Critical", StringComparison.Ordinal))
            .ToArray();

        string[] allowed = HonestCriticalStatements.TryGetValue(viewModelFileName, out string[]? entries)
            ? entries
            : [];

        string[] unexpected = found
            .Where(entry => !allowed.Contains(entry.Text, StringComparer.Ordinal))
            .Select(entry => $"{viewModelFileName}:{entry.Number}  {entry.Text}")
            .ToArray();

        Assert.True(
            unexpected.Length == 0,
            "A display-only row states that its subject will not run through PlanRow.Disposition; Critical is "
            + "the engine's word for an irreversible ACTION and must never be borrowed to obtain a colour. "
            + "Unlisted Critical statements: " + string.Join(" | ", unexpected));

        // The other direction: an allowlisted statement that has disappeared means the reason recorded beside
        // it is stale, and a stale allowlist is how the next honest-looking Critical slips through.
        string[] missing = allowed
            .Where(statement => !found.Any(entry => string.Equals(entry.Text, statement, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"{viewModelFileName} no longer contains allowlisted Critical statement(s): "
            + string.Join(" | ", missing)
            + ". Remove the entry rather than leaving a reason for code that is gone.");
    }

    /// <summary>The six view-models that build display-only <c>PlanRow</c>s (spec §6 production inventory).</summary>
    public static TheoryData<string> DisplayRowViewModels() =>
    [
        "MigrationViewModel.cs",
        "BackupViewModel.cs",
        "RestoreViewModel.cs",
        "InstallViewModel.cs",
        "UninstallWizardViewModel.cs",
        "UninstallViewModel.cs",
    ];

    /// <summary>
    /// The <see cref="RiskLevel.Critical"/> statements that are HONEST engine facts, each named with its
    /// reason. Both survivors describe the same real irreversible action — removing a Store app — which the
    /// engine genuinely will run; neither is a display-only row reaching for red.
    /// </summary>
    private static readonly Dictionary<string, string[]> HonestCriticalStatements = new(StringComparer.Ordinal)
    {
        // UninstallViewModel.StageAppx: the confirm-gate preview row for a removal that WILL run, and
        // RunAppxRemovalAsync: the typed AppxRemoveAction handed to the executor. Store-app removal cannot be
        // undone, so Critical is the truth about the action, not a lever on the chip.
        ["UninstallViewModel.cs"] =
        [
            "RiskLevel.Critical, package.PackageFullName),",
            "Risk = RiskLevel.Critical,",
        ],
    };

    /// <summary>The two facts a risk chip renders. They are separate properties precisely because they are
    /// separate truths — collapsing them back into one value is the defect a0d2c52 removed.</summary>
    private static readonly string[] RequiredRiskChipAttributes = ["Risk", "IsBlocked"];

    /// <summary>The CLR namespace the chip controls live in, as a XAML namespace URI writes it. The assembly
    /// suffix is stripped before comparing, because the same control is referenced both with and without one.</summary>
    private const string ControlsClrNamespace = "clr-namespace:WindowsCareKit.App.Controls";

    /// <summary>Every <c>RiskChip</c> ELEMENT under <c>src/</c>, with the <c>file:line</c> that locates it.
    /// Namespace-checked, so a same-named control from anywhere else is not counted, and binding expressions
    /// that merely mention the type (<c>AncestorType=ctl:RiskChip</c>) are not elements and never appear.</summary>
    private static IEnumerable<(string Site, XElement Element)> RiskChipSites()
    {
        foreach (string path in SourceFiles().Where(file => file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants().Where(IsRiskChip))
            {
                string site = $"{Path.GetRelativePath(RepoRoot, path)}:{((IXmlLineInfo)element).LineNumber}";
                yield return (site, element);
            }
        }
    }

    private static bool IsRiskChip(XElement element)
    {
        if (!string.Equals(element.Name.LocalName, nameof(RiskChip), StringComparison.Ordinal))
            return false;

        string declared = element.Name.NamespaceName;
        int assemblySuffix = declared.IndexOf(';', StringComparison.Ordinal);
        return string.Equals(
            assemblySuffix < 0 ? declared : declared[..assemblySuffix],
            ControlsClrNamespace,
            StringComparison.Ordinal);
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

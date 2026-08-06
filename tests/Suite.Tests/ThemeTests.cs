using System.Text.RegularExpressions;
using System.Xml.Linq;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Theming;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

public sealed class ThemeTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Regex HexColor = new("^#[0-9A-Fa-f]{6,8}$", RegexOptions.Compiled);

    [Fact]
    public void Dark_palette_resolves_to_frozen_baseline()
    {
        Dictionary<string, string> palette = LoadThemeColors(StrongboxPath);

        var expected = new Dictionary<string, string>
        {
            ["Bg.Window"] = "#101319",
            ["Bg.Panel"] = "#14181F",
            ["Bg.PanelAlt"] = "#1E242F",
            ["Bg.Card"] = "#181D26",
            ["Bg.Scrim"] = "#CC0A0D12",
            ["Bg.Scrim.Confirm"] = "#CC101319",
            ["Bg.Scrim.Loading"] = "#AA101319",
            ["Bg.Scrim.Wizard"] = "#D80A0D12",
            ["Bg.Card.Alt"] = "#171C25",
            ["Bg.Card.Warning"] = "#2B2413",
            ["Bg.Callout.Bad"] = "#33181C",
            ["Bg.Chrome.Rail"] = "#14181F",
            ["Bg.Footer"] = "#101319",
            ["Bg.Gold.Subtle"] = "#122A20",
            ["Bg.Progress.Track"] = "#262D3A",
            ["Bg.Protected"] = "#122A20",
            ["Bg.Row.Hover"] = "#1E242F",
            ["Bg.Row.Selected"] = "#122A20",
            ["Bg.Status.Bad"] = "#33181C",
            ["Bg.Status.Good"] = "#12291F",
            ["Bg.Status.Warn"] = "#2B2413",
            ["Bg.Warning.Strong"] = "#33181C",
            ["Border.Callout.Bad"] = "#6E2C2C",
            ["Border.Card.Warning"] = "#57491F",
            ["Border.Protected"] = "#245740",
            ["Border.Soft"] = "#262D3A",
            ["Border.TopBar"] = "#262D3A",
            ["Border.Gold"] = "#245740",
            ["Code.Bg"] = "#101319",
            ["Code.Fg"] = "#E9EDF3",
            ["Gold"] = "#34C98E",
            ["Gold.Bright"] = "#7FDCAE",
            ["Gold.Dim"] = "#23A06F",
            ["Text"] = "#E9EDF3",
            ["Text.Muted"] = "#A6AFBD",
            ["Text.Faint"] = "#727C8C",
            ["Text.Disabled"] = "#727C8C",
            ["Success"] = "#7FDCAE",
            ["Danger"] = "#FF8A7A",
            ["Accent.Teal"] = "#34C98E",
            ["Accent.Amber"] = "#E5C76B",
            ["Bg.Input"] = "#181D26",
            ["ScrollBar.Track"] = "#14181F",
            ["ScrollBar.Thumb"] = "#3A4351",
            ["ScrollBar.Thumb.Hover"] = "#727C8C",
            ["Wck.Rail.Background"] = "#14181F",
            ["Wck.Rail.Border"] = "#262D3A",
            ["Wck.Rail.Hover"] = "#1E242F",
            ["Wck.Rail.Selected"] = "#122A20",
            ["Wck.Rail.Accent"] = "#7FDCAE",
            ["Wck.Rail.Text"] = "#E9EDF3",
            ["Wck.Rail.Quiet"] = "#727C8C",
            ["Wck.Info.Fg"] = "#9AB1C7",
            ["Wck.Info.Wash"] = "#171E27",
            ["Wck.Info.Border"] = "#2C3A49",
            ["Wck.Attention.Fg"] = "#E5C76B",
            ["Wck.Attention.Wash"] = "#2B2413",
            ["Wck.Attention.Border"] = "#57491F",
            ["Brand.Mark.Start"] = "#3BD598",
            ["Brand.Mark.End"] = "#1F9464",
            ["Backup.Backdrop"] = "#0A0D12",
            ["Backup.Window.Border"] = "#262D3A",
            ["Backup.Chrome"] = "#14181F",
            ["Backup.Card"] = "#181D26",
            ["Backup.CardHi"] = "#1E242F",
            ["Backup.Line"] = "#262D3A",
            ["Backup.LineStrong"] = "#3A4351",
            ["Backup.Ink"] = "#E9EDF3",
            ["Backup.Muted"] = "#A6AFBD",
            ["Backup.Faint"] = "#727C8C",
            ["Backup.Em"] = "#34C98E",
            ["Backup.EmDeep"] = "#23A06F",
            ["Backup.EmInk"] = "#07231A",
            ["Backup.EmWash"] = "#122A20",
            ["Backup.EmBorder"] = "#245740",
            ["Backup.Primary.Border"] = "#1C8A5E",
            ["Backup.Primary.HoverBorder"] = "#23A06F",
            ["Backup.OkFg"] = "#7FDCAE",
            ["Backup.OkWash"] = "#12291F",
            ["Backup.OkBorder"] = "#23543C",
            ["Backup.MedFg"] = "#E5C76B",
            ["Backup.MedWash"] = "#2B2413",
            ["Backup.MedBorder"] = "#57491F",
            ["Backup.MedDot"] = "#E5C76B",
            ["Backup.Danger"] = "#E5484D",
            ["Backup.DangerDeep"] = "#B3383C",
            ["Backup.DangerStrong"] = "#FF8A7A",
            ["Backup.DangerText"] = "#FFF4F2",
            ["Backup.DangerBorder"] = "#6E2C2C",
            ["Backup.DangerBorderHover"] = "#8F3A3A",
            ["Backup.RailBorder"] = "#262D3A",
            ["Backup.PathBg"] = "#101319",
        };

        Assert.Equal(expected.Count, palette.Count);
        foreach ((string key, string value) in expected)
            Assert.Equal(value, palette[key]);
    }

    [Fact]
    public void Daylight_defines_exactly_the_same_keys_as_Strongbox()
    {
        string[] strongboxKeys = LoadKeys(StrongboxPath);
        string[] daylightKeys = LoadKeys(DaylightPath);

        Assert.Equal(strongboxKeys, daylightKeys);
    }

    [Fact]
    public void Themes_are_structurally_identical_except_colors()
    {
        XElement strongbox = CanonicalTheme(StrongboxPath);
        XElement daylight = CanonicalTheme(DaylightPath);

        Assert.True(
            XNode.DeepEquals(strongbox, daylight),
            "Daylight.xaml must remain a full-copy structural match for Strongbox.xaml except color values.");
    }

    [Fact]
    public void Daylight_palette_meets_contrast_floor()
    {
        Dictionary<string, string> palette = LoadThemeColors(DaylightPath);

        AssertContrast(palette, "Text", "Bg.Window", 4.5);
        AssertContrast(palette, "Text.Muted", "Bg.Panel", 4.5);
        AssertContrast(palette, "Gold", "Bg.Card", 4.5);
        AssertContrast(palette, "Danger", "Bg.Window", 4.5);
        AssertContrast(palette, "Success", "Bg.Window", 4.5);
        AssertContrast(palette, "Text.Faint", "Bg.Panel", 3.0);
        AssertContrast(palette, "Text.Disabled", "Bg.Window", 3.0);
        AssertContrast(palette, "Text", "Bg.Input", 4.5);
        AssertContrast(palette, "Gold.Dim", "Bg.Window", 4.5);
        AssertContrast(palette, "Gold.Dim", "Bg.Card", 4.5);
        AssertContrast(palette, "Accent.Amber", "Bg.Card.Warning", 4.5);
        AssertContrast(palette, "Success", "Bg.Status.Good", 4.5);
        AssertContrast(palette, "Gold", "Bg.Status.Warn", 4.5);
        AssertContrast(palette, "Danger", "Bg.Status.Bad", 4.5);
        AssertContrast(palette, "Code.Fg", "Code.Bg", 4.5);

        // M1 chip families. A chip's own label sits on its own wash, so that is the pair that decides whether
        // the chip is readable at all; the window pairing covers the same ink used as plain caption text.
        AssertContrast(palette, "Wck.Info.Fg", "Wck.Info.Wash", 4.5);
        AssertContrast(palette, "Wck.Info.Fg", "Bg.Window", 4.5);
        AssertContrast(palette, "Wck.Attention.Fg", "Wck.Attention.Wash", 4.5);
        AssertContrast(palette, "Wck.Attention.Fg", "Bg.Window", 4.5);
    }

    /// <summary>Dark had no contrast floor at all before M1 — the light theme was gated and the dark theme was
    /// taken on trust. This does not backfill the whole palette (that would be a separate, larger change); it
    /// gates the two families M1 introduces, so the new tokens cannot be the ones that ship unreadable.</summary>
    [Fact]
    public void Strongbox_chip_families_meet_contrast_floor()
    {
        Dictionary<string, string> palette = LoadThemeColors(StrongboxPath);

        AssertContrast(palette, "Wck.Info.Fg", "Wck.Info.Wash", 4.5);
        AssertContrast(palette, "Wck.Info.Fg", "Bg.Window", 4.5);
        AssertContrast(palette, "Wck.Attention.Fg", "Wck.Attention.Wash", 4.5);
        AssertContrast(palette, "Wck.Attention.Fg", "Bg.Window", 4.5);
    }

    /// <summary>
    /// <c>Wck.Attention.*</c> re-keys the <c>Backup.Med*</c> values rather than inventing a colour, and both
    /// keys must ship until the four out-of-scope screens migrate. The theme dictionary format forces the hex
    /// to be written twice (a <c>{StaticResource}</c> indirection would defeat the frozen-baseline assertion
    /// above, which reads literal hex), so this test is what keeps the duplication from becoming drift: edit
    /// one and forget the other, and this goes red instead of the two families silently diverging.
    /// It is deleted together with the <c>Backup.Med*</c> alias, not before.
    /// </summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void Attention_family_stays_identical_to_its_Backup_alias(string themeName)
    {
        Dictionary<string, string> palette = LoadThemeColors(
            themeName == "Strongbox" ? StrongboxPath : DaylightPath);

        Assert.Equal(palette["Backup.MedFg"], palette["Wck.Attention.Fg"]);
        Assert.Equal(palette["Backup.MedWash"], palette["Wck.Attention.Wash"]);
        Assert.Equal(palette["Backup.MedBorder"], palette["Wck.Attention.Border"]);
    }

    [Fact]
    public void Theme_dictionaries_define_an_implicit_scrollbar_style()
    {
        AssertImplicitScrollbarStyle(StrongboxPath);
        AssertImplicitScrollbarStyle(DaylightPath);
    }

    [Fact]
    public void Theme_xaml_does_not_use_dynamic_resources()
    {
        Assert.DoesNotContain("DynamicResource", File.ReadAllText(StrongboxPath), StringComparison.Ordinal);
        Assert.DoesNotContain("DynamicResource", File.ReadAllText(DaylightPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Theme_resolution_precedence_and_fallback()
    {
        Assert.Equal(AppTheme.Dark, ThemeService.Resolve(new[] { "--theme", "dark" }, "light", AppTheme.Light));
        Assert.Equal(AppTheme.Light, ThemeService.Resolve(new[] { "--theme=light" }, "dark", AppTheme.Dark));
        Assert.Equal(AppTheme.Dark, ThemeService.Resolve(Array.Empty<string>(), "dark", AppTheme.Light));
        Assert.Equal(AppTheme.Light, ThemeService.Resolve(Array.Empty<string>(), null, AppTheme.Light));
        Assert.Equal(AppTheme.Dark, ThemeService.Resolve(Array.Empty<string>(), null, null));
        Assert.Equal(AppTheme.Dark, ThemeService.Resolve(new[] { "--theme", "blue" }, null, AppTheme.Light));
        Assert.Equal(AppTheme.Dark, ThemeService.Resolve(new[] { "--theme", "" }, null, AppTheme.Light));
        Assert.Equal(AppTheme.Dark, ThemeService.Resolve(Array.Empty<string>(), "blue", AppTheme.Light));
        Assert.Equal(AppTheme.Dark, ThemeService.Resolve(Array.Empty<string>(), "   ", AppTheme.Light));
    }

    [Fact]
    public void Theme_preference_round_trips_and_corrupt_falls_back_to_dark()
    {
        using var ws = new TempWorkspace("wck-theme-store-");
        var store = new JsonThemePreferenceStore(ws.Combine("settings"));

        Assert.Equal(AppTheme.Dark, new ThemeService(Array.Empty<string>(), null, store).SelectedTheme);

        Assert.True(store.TryWriteTheme(AppTheme.Light));
        Assert.Equal(AppTheme.Light, new ThemeService(Array.Empty<string>(), null, store).SelectedTheme);

        Assert.True(store.TryWriteTheme(AppTheme.Dark));
        Assert.Equal(AppTheme.Dark, new ThemeService(Array.Empty<string>(), null, store).SelectedTheme);

        File.WriteAllText(store.SettingsPath, "{");
        Assert.Equal(AppTheme.Dark, new ThemeService(Array.Empty<string>(), null, store).SelectedTheme);

        File.WriteAllText(store.SettingsPath, "{\"theme\":\"blue\"}");
        Assert.Equal(AppTheme.Dark, new ThemeService(Array.Empty<string>(), null, store).SelectedTheme);

        string fileAsBaseDirectory = ws.WriteFile("not-a-directory", "x");
        var failingStore = new JsonThemePreferenceStore(fileAsBaseDirectory);
        var failingService = new ThemeService(Array.Empty<string>(), null, failingStore);

        Assert.False(failingService.TrySelectTheme(AppTheme.Light));
        Assert.Equal(AppTheme.Dark, failingService.SelectedTheme);
        Assert.False(failingService.RestartRequired);
    }

    [Fact]
    public void Theme_service_state_machine()
    {
        using var ws = new TempWorkspace("wck-theme-state-");
        var store = new JsonThemePreferenceStore(ws.Combine("settings"));
        var service = new ThemeService(Array.Empty<string>(), null, store);

        Assert.Equal(AppTheme.Dark, service.AppliedTheme);
        Assert.Equal(AppTheme.Dark, service.SelectedTheme);
        Assert.False(service.RestartRequired);

        Assert.True(service.TrySelectTheme(AppTheme.Light));
        Assert.Equal(AppTheme.Light, service.SelectedTheme);
        Assert.True(service.RestartRequired);
        Assert.Equal(AppTheme.Light, new ThemeService(Array.Empty<string>(), null, store).SelectedTheme);

        Assert.True(service.TrySelectTheme(AppTheme.Light));
        Assert.Equal(AppTheme.Light, service.SelectedTheme);
        Assert.True(service.RestartRequired);

        Assert.True(service.TrySelectTheme(AppTheme.Dark));
        Assert.Equal(AppTheme.Dark, service.SelectedTheme);
        Assert.False(service.RestartRequired);
    }

    [Fact]
    public void SettingsViewModel_exposes_themes_and_persists_selection()
    {
        I18n i18n = TestI18n.Full("en");
        var themeService = new RecordingThemeService();
        var vm = new SettingsViewModel(
            i18n,
            themeService,
            new RecordingUrlOpener(),
            TestHelpers.NoComponentsDiscovered());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Collection(
            vm.AvailableThemes,
            dark =>
            {
                Assert.Equal(AppTheme.Dark, dark.Theme);
                Assert.Equal("Dark", dark.DisplayName);
            },
            light =>
            {
                Assert.Equal(AppTheme.Light, light.Theme);
                Assert.Equal("Light", light.DisplayName);
            });

        vm.SelectedTheme = AppTheme.Light;

        Assert.Equal(AppTheme.Light, themeService.LastSelectedTheme);
        Assert.Equal(AppTheme.Light, vm.SelectedTheme);
        Assert.True(vm.RestartRequired);
        Assert.Equal("Restart the app to apply the selected theme.", vm.ThemeStatusText);
        Assert.Contains(nameof(SettingsViewModel.SelectedTheme), raised);
        Assert.Contains(nameof(SettingsViewModel.RestartRequired), raised);
        Assert.Contains(nameof(SettingsViewModel.ThemeStatusText), raised);

        i18n.SelectedCulture = "tr";

        Assert.Contains(vm.AvailableThemes, theme => theme.Theme == AppTheme.Dark && theme.DisplayName == "Koyu");
        Assert.Equal("Seçili temayı uygulamak için uygulamayı yeniden başlat.", vm.ThemeStatusText);
    }

    private static string StrongboxPath => Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "Themes", "Strongbox.xaml");
    private static string DaylightPath => Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "Themes", "Daylight.xaml");

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

    private static Dictionary<string, string> LoadThemeColors(string path)
        => XDocument.Load(path)
            .Descendants()
            .Where(e => e.Name.LocalName is "SolidColorBrush" or "Color")
            .Select(e => new
            {
                Key = (string?)e.Attribute(Xaml + "Key"),
                Color = e.Name.LocalName == "Color"
                    ? e.Value.Trim()
                    : (string?)e.Attribute("Color")
            })
            .Where(e => e.Key is not null && e.Color is not null)
            .ToDictionary(e => e.Key!, e => e.Color!, StringComparer.Ordinal);

    private static string[] LoadKeys(string path)
        => XDocument.Load(path)
            .Descendants()
            .Select(e => (string?)e.Attribute(Xaml + "Key"))
            .Where(key => key is not null)
            .Select(key => key!)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

    private static XElement CanonicalTheme(string path)
    {
        var root = new XElement(XDocument.Load(path).Root!);
        NormalizeHexValues(root);
        return root;
    }

    private static void NormalizeHexValues(XElement element)
    {
        foreach (XAttribute attribute in element.Attributes().ToList())
        {
            if (HexColor.IsMatch(attribute.Value))
                attribute.Value = "#HEX";
        }

        if (HexColor.IsMatch(element.Value.Trim()) && !element.HasElements)
            element.Value = "#HEX";

        if (element.Name.LocalName == "Double" && element.Attribute(Xaml + "Key") is not null)
            element.Value = "#VALUE";

        foreach (XElement child in element.Elements())
            NormalizeHexValues(child);
    }

    private static void AssertImplicitScrollbarStyle(string path)
    {
        XElement style = XDocument.Load(path)
            .Descendants()
            .SingleOrDefault(e =>
                e.Name.LocalName == "Style" &&
                e.Attribute(Xaml + "Key") is null &&
                e.Attribute("TargetType")?.Value == "ScrollBar")
            ?? throw new Xunit.Sdk.XunitException($"{Path.GetFileName(path)} does not define an implicit ScrollBar style.");

        Assert.Contains(
            style.Descendants(),
            e => e.Name.LocalName == "Setter" &&
                 e.Attribute("Property")?.Value == "Background" &&
                 e.Attribute("Value")?.Value == "{StaticResource ScrollBar.Track}");
    }

    private static void AssertContrast(Dictionary<string, string> palette, string foregroundKey, string backgroundKey, double floor)
    {
        double ratio = Contrast.Ratio(palette[foregroundKey], palette[backgroundKey]);
        Assert.True(
            ratio >= floor,
            $"{foregroundKey} on {backgroundKey} contrast was {ratio:F2}:1, below {floor:F1}:1.");
    }

    private sealed class RecordingThemeService : IThemeService
    {
        public IReadOnlyList<AppTheme> AvailableThemes { get; } = new[] { AppTheme.Dark, AppTheme.Light };
        public AppTheme SelectedTheme { get; private set; } = AppTheme.Dark;
        public AppTheme AppliedTheme { get; } = AppTheme.Dark;
        public bool RestartRequired => SelectedTheme != AppliedTheme;
        public AppTheme? LastSelectedTheme { get; private set; }

        public bool TrySelectTheme(AppTheme theme)
        {
            LastSelectedTheme = theme;
            SelectedTheme = theme;
            return true;
        }
    }
}

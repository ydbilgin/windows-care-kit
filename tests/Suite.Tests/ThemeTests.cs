using System.Globalization;
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
            ["Bg.Window"] = "#141009",
            ["Bg.Panel"] = "#1A160F",
            ["Bg.PanelAlt"] = "#242019",
            ["Bg.Card"] = "#272219",
            ["Bg.Scrim"] = "#CC0B0906",
            ["Bg.Scrim.Confirm"] = "#CC0F0B05",
            ["Bg.Scrim.Loading"] = "#AA141009",
            ["Bg.Scrim.Wizard"] = "#D80C0805",
            ["Bg.Card.Alt"] = "#1D1810",
            ["Bg.Card.Warning"] = "#241F12",
            ["Bg.Callout.Bad"] = "#2A1C12",
            ["Bg.Chrome.Rail"] = "#1C1810",
            ["Bg.Footer"] = "#17120B",
            ["Bg.Gold.Subtle"] = "#241F14",
            ["Bg.Progress.Track"] = "#221D14",
            ["Bg.Protected"] = "#16201C",
            ["Bg.Row.Hover"] = "#211D17",
            ["Bg.Row.Selected"] = "#2E2614",
            ["Bg.Status.Bad"] = "#351D1D",
            ["Bg.Status.Good"] = "#173322",
            ["Bg.Status.Warn"] = "#332A16",
            ["Bg.Warning.Strong"] = "#241B0A",
            ["Border.Callout.Bad"] = "#7A4B22",
            ["Border.Card.Warning"] = "#54472A",
            ["Border.Protected"] = "#22332D",
            ["Border.Soft"] = "#3A3329",
            ["Border.TopBar"] = "#221D14",
            ["Border.Gold"] = "#5A4E33",
            ["Code.Bg"] = "#0F1830",
            ["Code.Fg"] = "#CFE0FF",
            ["Gold"] = "#E6B25E",
            ["Gold.Bright"] = "#F3C97E",
            ["Gold.Dim"] = "#D29A41",
            ["Text"] = "#F4EEE0",
            ["Text.Muted"] = "#B8AD96",
            ["Text.Faint"] = "#867C67",
            ["Text.Disabled"] = "#E6B25E",
            ["Success"] = "#94BE8C",
            ["Danger"] = "#E08C8C",
            ["Accent.Teal"] = "#7FC2A8",
            ["Accent.Amber"] = "#E8B36B",
            ["Bg.Input"] = "#11100B",
            ["ScrollBar.Track"] = "#1A160F",
            ["ScrollBar.Thumb"] = "#3A3329",
            ["ScrollBar.Thumb.Hover"] = "#5A4E33",
            ["Brand.Mark.Start"] = "#F3C97E",
            ["Brand.Mark.End"] = "#D29A41"
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
        var i18n = new I18n(Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang"));
        i18n.Load("en");
        var themeService = new RecordingThemeService();
        var vm = new SettingsViewModel(i18n, themeService);
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
        double ratio = ContrastRatio(palette[foregroundKey], palette[backgroundKey]);
        Assert.True(
            ratio >= floor,
            $"{foregroundKey} on {backgroundKey} contrast was {ratio:F2}:1, below {floor:F1}:1.");
    }

    private static double ContrastRatio(string foreground, string background)
    {
        double fg = RelativeLuminance(foreground);
        double bg = RelativeLuminance(background);
        double lighter = Math.Max(fg, bg);
        double darker = Math.Min(fg, bg);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(string hex)
    {
        string rgb = hex.Length == 9 ? hex[3..] : hex[1..];
        double r = Linear(byte.Parse(rgb[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0);
        double g = Linear(byte.Parse(rgb[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0);
        double b = Linear(byte.Parse(rgb[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    private static double Linear(double channel)
        => channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

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

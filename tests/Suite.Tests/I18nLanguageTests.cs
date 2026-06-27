using WindowsCareKit.App.Localization;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Covers the N-language discovery that backs the brand-strip language selector. Adding a language
/// must be a data-only change — drop a <c>&lt;code&gt;.json</c> with a <c>meta.languageName</c> entry,
/// no code edit — so these tests pin discovery, display-name fallback, and ordering.
/// </summary>
public sealed class I18nLanguageTests
{
    private static string NewLangDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wck-lang-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteLang(string dir, string code, string? displayName, params (string, string)[] extra)
    {
        var pairs = new List<string>();
        if (displayName is not null)
            pairs.Add($"  \"meta.languageName\": {System.Text.Json.JsonSerializer.Serialize(displayName)}");
        foreach ((string k, string v) in extra)
            pairs.Add($"  {System.Text.Json.JsonSerializer.Serialize(k)}: {System.Text.Json.JsonSerializer.Serialize(v)}");
        File.WriteAllText(Path.Combine(dir, code + ".json"), "{\n" + string.Join(",\n", pairs) + "\n}");
    }

    [Fact]
    public void Discovers_every_language_file_with_its_display_name()
    {
        string dir = NewLangDir();
        WriteLang(dir, "en", "English");
        WriteLang(dir, "tr", "Türkçe");
        WriteLang(dir, "de", "Deutsch");

        IReadOnlyList<LanguageOption> langs = I18n.EnumerateLanguages(dir);

        Assert.Equal(3, langs.Count);
        Assert.Contains(langs, l => l.Code == "tr" && l.DisplayName == "Türkçe");
        Assert.Contains(langs, l => l.Code == "de" && l.DisplayName == "Deutsch");
    }

    [Fact]
    public void English_sorts_first_then_alphabetical_by_display_name()
    {
        string dir = NewLangDir();
        WriteLang(dir, "tr", "Türkçe");
        WriteLang(dir, "de", "Deutsch");
        WriteLang(dir, "en", "English");

        string[] order = I18n.EnumerateLanguages(dir).Select(l => l.Code).ToArray();

        Assert.Equal("en", order[0]);                 // English always first
        Assert.Equal(new[] { "en", "de", "tr" }, order); // then Deutsch < Türkçe
    }

    [Fact]
    public void Falls_back_to_code_when_display_name_is_missing()
    {
        string dir = NewLangDir();
        WriteLang(dir, "fr", displayName: null, ("app.title", "X")); // no meta.languageName

        LanguageOption fr = Assert.Single(I18n.EnumerateLanguages(dir));
        Assert.Equal("fr", fr.Code);
        Assert.Equal("fr", fr.DisplayName);
    }

    [Fact]
    public void Falls_back_to_code_when_file_is_malformed()
    {
        string dir = NewLangDir();
        File.WriteAllText(Path.Combine(dir, "xx.json"), "{ this is not valid json ");

        LanguageOption xx = Assert.Single(I18n.EnumerateLanguages(dir));
        Assert.Equal("xx", xx.Code);
        Assert.Equal("xx", xx.DisplayName);
    }

    [Fact]
    public void Missing_directory_yields_a_non_empty_english_default()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wck-lang-missing-" + Guid.NewGuid().ToString("N"));

        LanguageOption only = Assert.Single(I18n.EnumerateLanguages(dir));
        Assert.Equal("en", only.Code);
        Assert.Equal("English", only.DisplayName);
    }

    [Fact]
    public void Shipped_language_files_expose_proper_display_names()
    {
        string langDir = Path.Combine(FindRepositoryRoot(), "src", "Suite.App.Wpf", "lang");

        IReadOnlyList<LanguageOption> langs = I18n.EnumerateLanguages(langDir);

        Assert.Contains(langs, l => l.Code == "en" && l.DisplayName == "English");
        Assert.Contains(langs, l => l.Code == "tr" && l.DisplayName == "Türkçe");
        Assert.Equal("en", langs[0].Code); // English presented first in the selector
    }

    [Fact]
    public void Load_partial_language_uses_english_fallback_for_missing_keys()
    {
        string dir = NewLangDir();
        WriteLang(
            dir,
            "en",
            "English",
            ("shared.key", "English shared"),
            ("english.only", "English fallback"));
        WriteLang(
            dir,
            "xx",
            "Example",
            ("shared.key", "XX override"));

        var i18n = new I18n(dir);

        i18n.Load("xx");

        Assert.Equal("xx", i18n.Culture);
        Assert.Equal("XX override", i18n["shared.key"]);
        Assert.Equal("English fallback", i18n["english.only"]);
        Assert.Equal("missing.in.both", i18n["missing.in.both"]);
        // meta.* is file-descriptive, stripped from the UI map — it must never leak as a bound string.
        Assert.Equal("meta.languageName", i18n["meta.languageName"]);
    }

    [Fact]
    public void Load_english_uses_english_base()
    {
        string dir = NewLangDir();
        WriteLang(dir, "en", "English", ("app.title", "Windows Care Kit"));
        WriteLang(dir, "xx", "Example", ("app.title", "Example title"));

        var i18n = new I18n(dir);

        i18n.Load("en");

        Assert.Equal("en", i18n.Culture);
        Assert.Equal("Windows Care Kit", i18n["app.title"]);
    }

    [Fact]
    public void Load_missing_language_file_falls_back_to_english()
    {
        string dir = NewLangDir();
        WriteLang(dir, "en", "English", ("app.title", "Windows Care Kit"));

        var i18n = new I18n(dir);

        i18n.Load("zz");

        Assert.Equal("en", i18n.Culture);
        Assert.Equal("Windows Care Kit", i18n["app.title"]);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WindowsCareKit.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("WindowsCareKit.slnx not found above the test output.");
    }
}

using Xunit;
using TheApp = WindowsCareKit.App.App;

namespace WindowsCareKit.Tests;

/// <summary>
/// Covers <see cref="TheApp.ResolveCulture(string[], string?, string)"/> — the UI-language
/// picker. The override chain is CLI <c>--lang</c> &gt; <c>WCK_LANG</c> &gt; English default.
/// </summary>
public sealed class AppCultureResolutionTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public void Cli_lang_space_form_wins(string code)
        => Assert.Equal(code, TheApp.ResolveCulture(new[] { "--lang", code }, envLang: null, osTwoLetter: "tr"));

    [Theory]
    [InlineData("--lang=en", "en")]
    [InlineData("--lang=tr", "tr")]
    [InlineData("--LANG=EN", "en")]
    public void Cli_lang_equals_form_wins_case_insensitively(string arg, string expected)
        => Assert.Equal(expected, TheApp.ResolveCulture(new[] { arg }, envLang: null, osTwoLetter: "tr"));

    [Fact]
    public void Cli_lang_overrides_env_and_os()
        => Assert.Equal("en", TheApp.ResolveCulture(new[] { "--lang", "en" }, envLang: "tr", osTwoLetter: "tr"));

    [Theory]
    [InlineData("EN", "en")]
    [InlineData("tr", "tr")]
    public void Env_wins_when_no_cli_arg(string envLang, string expected)
        => Assert.Equal(expected, TheApp.ResolveCulture(Array.Empty<string>(), envLang, osTwoLetter: "tr"));

    [Fact]
    public void No_override_defaults_to_english_even_on_turkish_os()
        => Assert.Equal("en", TheApp.ResolveCulture(Array.Empty<string>(), envLang: null, osTwoLetter: "tr"));

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    public void No_override_defaults_to_english_regardless_of_os(string os)
        => Assert.Equal("en", TheApp.ResolveCulture(Array.Empty<string>(), envLang: null, osTwoLetter: os));

    [Theory]
    [InlineData("fr")]   // unsupported language code
    [InlineData("")]
    [InlineData("   ")]
    public void Unsupported_or_blank_override_falls_through_to_english(string bad)
        => Assert.Equal("en", TheApp.ResolveCulture(new[] { "--lang", bad }, envLang: bad, osTwoLetter: "tr"));

    // --- ExtractOption: the shared --name value / --name=value parser (backs --lang and --screen) ---

    [Fact]
    public void ExtractOption_reads_space_form()
        => Assert.Equal("migration", TheApp.ExtractOption(new[] { "--screen", "migration" }, "--screen"));

    [Fact]
    public void ExtractOption_reads_equals_form()
        => Assert.Equal("backup", TheApp.ExtractOption(new[] { "--screen=backup" }, "--screen"));

    [Fact]
    public void ExtractOption_is_case_insensitive_on_the_name()
        => Assert.Equal("clean", TheApp.ExtractOption(new[] { "--SCREEN", "clean" }, "--screen"));

    [Fact]
    public void ExtractOption_returns_null_when_absent()
        => Assert.Null(TheApp.ExtractOption(new[] { "--lang", "en" }, "--screen"));

    [Fact]
    public void ExtractOption_returns_null_when_name_is_last_token_without_value()
        => Assert.Null(TheApp.ExtractOption(new[] { "--screen" }, "--screen"));

    [Fact]
    public void ExtractOption_takes_the_first_occurrence()
        => Assert.Equal("install", TheApp.ExtractOption(new[] { "--screen", "install", "--screen", "clean" }, "--screen"));
}

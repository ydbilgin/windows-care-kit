using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using Xunit;

namespace WindowsCareKit.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void App_info_matches_project_metadata()
    {
        var vm = new SettingsViewModel(new I18n());

        Assert.False(string.IsNullOrWhiteSpace(vm.Version));
        Assert.DoesNotContain("+", vm.Version); // build metadata trimmed
        Assert.Equal("MIT", vm.License);
        Assert.Equal("https://github.com/ydbilgin/windows-care-kit", vm.RepositoryUrl);
        Assert.Equal("https://github.com/ydbilgin/windows-care-kit/releases", vm.ReleasesUrl);
    }

    [Theory]
    [InlineData("0.1.0+592dc3bdeadbeef", "0.1.0")] // SemVer build metadata dropped
    [InlineData("1.2.3", "1.2.3")]                  // no metadata → unchanged
    [InlineData("", "")]
    public void Version_drops_build_metadata(string raw, string expected)
        => Assert.Equal(expected, SettingsViewModel.TrimBuildMetadata(raw));

    [Fact]
    public void Language_selector_uses_shared_i18n_languages()
    {
        var i18n = new I18n();
        var vm = new SettingsViewModel(i18n);

        Assert.Same(i18n, vm.I18n);
        Assert.Same(i18n.AvailableLanguages, vm.I18n.AvailableLanguages);
        Assert.Contains(vm.I18n.AvailableLanguages, language => language.Code == "en");
        Assert.Contains(vm.I18n.AvailableLanguages, language => language.Code == "tr");
    }

    [Fact]
    public void Setting_selected_culture_through_view_model_switches_language()
    {
        var i18n = new I18n();
        var vm = new SettingsViewModel(i18n);

        vm.I18n.Load("en");
        vm.I18n.SelectedCulture = "tr";

        Assert.Equal("tr", vm.I18n.Culture);
        Assert.Equal("tr", i18n.Culture);
    }
}

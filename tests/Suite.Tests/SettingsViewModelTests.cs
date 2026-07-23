using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Theming;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Execution;
using Xunit;

namespace WindowsCareKit.Tests;

internal sealed class RecordingUrlOpener : IUrlOpener
{
    public List<Uri> Opened { get; } = new();

    public void Open(Uri uri) => Opened.Add(uri);
}

public sealed class SettingsViewModelTests
{
    [Fact]
    public void App_info_matches_project_metadata()
    {
        var vm = new SettingsViewModel(new I18n(), new FakeThemeService(), new RecordingUrlOpener());

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
        var vm = new SettingsViewModel(i18n, new FakeThemeService(), new RecordingUrlOpener());

        Assert.Same(i18n, vm.I18n);
        Assert.Same(i18n.AvailableLanguages, vm.I18n.AvailableLanguages);
        Assert.Contains(vm.I18n.AvailableLanguages, language => language.Code == "en");
        Assert.Contains(vm.I18n.AvailableLanguages, language => language.Code == "tr");
    }

    [Fact]
    public void Setting_selected_culture_through_view_model_switches_language()
    {
        I18n i18n = TestI18n.Full("en");
        var vm = new SettingsViewModel(i18n, new FakeThemeService(), new RecordingUrlOpener());

        vm.I18n.SelectedCulture = "tr";

        Assert.Equal("tr", vm.I18n.Culture);
        Assert.Equal("tr", i18n.Culture);
    }

    [Fact]
    public void Open_external_link_command_opens_repository_url()
    {
        var opener = new RecordingUrlOpener();
        var vm = new SettingsViewModel(new I18n(), new FakeThemeService(), opener);

        vm.OpenExternalLinkCommand.Execute(SettingsViewModel.ProjectRepositoryUrl);

        Assert.Equal(new Uri(SettingsViewModel.ProjectRepositoryUrl, UriKind.Absolute), Assert.Single(opener.Opened));
    }

    [Fact]
    public void Open_external_link_command_opens_releases_url()
    {
        var opener = new RecordingUrlOpener();
        var vm = new SettingsViewModel(new I18n(), new FakeThemeService(), opener);

        vm.OpenExternalLinkCommand.Execute(SettingsViewModel.ProjectReleasesUrl);

        Assert.Equal(new Uri(SettingsViewModel.ProjectReleasesUrl, UriKind.Absolute), Assert.Single(opener.Opened));
    }

    [Fact]
    public void Open_external_link_command_ignores_invalid_or_non_https_parameters()
    {
        var opener = new RecordingUrlOpener();
        var vm = new SettingsViewModel(new I18n(), new FakeThemeService(), opener);
        object?[] parameters = { null, "docs/readme.md", "file:///C:/x", "http://example.com", 42 };

        foreach (object? parameter in parameters)
        {
            int callsBefore = opener.Opened.Count;
            Exception? exception = Record.Exception(() => vm.OpenExternalLinkCommand.Execute(parameter));

            Assert.Null(exception);
            Assert.Equal(callsBefore, opener.Opened.Count);
        }
    }

    [Fact]
    public void Open_external_link_command_accepts_absolute_https_uri_objects()
    {
        var opener = new RecordingUrlOpener();
        var vm = new SettingsViewModel(new I18n(), new FakeThemeService(), opener);
        var uri = new Uri("https://example.com/object", UriKind.Absolute);

        vm.OpenExternalLinkCommand.Execute(uri);

        Assert.Same(uri, Assert.Single(opener.Opened));
    }

    [Fact]
    public void Constructor_throws_when_url_opener_is_null()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SettingsViewModel(new I18n(), new FakeThemeService(), null!));
    }

    private sealed class FakeThemeService : IThemeService
    {
        public IReadOnlyList<AppTheme> AvailableThemes { get; } = new[] { AppTheme.Dark, AppTheme.Light };
        public AppTheme SelectedTheme { get; private set; } = AppTheme.Dark;
        public AppTheme AppliedTheme { get; } = AppTheme.Dark;
        public bool RestartRequired => SelectedTheme != AppliedTheme;
        public bool TrySelectTheme(AppTheme theme)
        {
            SelectedTheme = theme;
            return true;
        }
    }
}

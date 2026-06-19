using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Smoke tests against the real machine. They assert the readers run and return sane data without
/// throwing — exact contents depend on the box, so assertions stay lenient.
/// </summary>
public class Win32ReadersSmokeTests
{
    [Fact]
    public void InstalledAppReader_returns_named_entries()
    {
        var apps = new Win32InstalledAppReader().ReadAll();
        Assert.NotNull(apps);
        // A real Windows box always has classic programs registered.
        Assert.NotEmpty(apps);
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.DisplayName)));
    }

    [Fact]
    public void AppxReader_does_not_throw_and_returns_a_list()
    {
        var packages = new Win32AppxReader().ReadCurrentUserPackages();
        Assert.NotNull(packages);
        // Don't assert non-empty: a stripped image could have none. Just check shape if present.
        Assert.All(packages, p => Assert.False(string.IsNullOrWhiteSpace(p.PackageFullName)));
    }

    [Fact]
    public void LeftoverProbe_finds_a_real_install_location_directory()
    {
        string temp = Path.Combine(Path.GetTempPath(), "wck-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var app = TestData.App(installLocation: temp);
            var dirs = new Win32LeftoverProbe().FindLeftoverDirectories(app);
            Assert.Contains(dirs, d => string.Equals(d.Path, Path.GetFullPath(temp), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(temp);
        }
    }

    [Fact]
    public void LeftoverProbe_tasks_are_deferred_to_a_later_pr()
        => Assert.Empty(new Win32LeftoverProbe().FindRelatedTasks(TestData.App()));
}

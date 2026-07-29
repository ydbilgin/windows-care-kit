using WindowsCareKit.App.Deployment;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

public sealed class AppPayloadRootsTests
{
    private const string AppDir = @"C:\Program Files\WindowsCareKit";
    private const string AppExe = @"C:\Program Files\WindowsCareKit\WindowsCareKit.exe";
    private const string LinkDir = @"C:\Users\u\AppData\Local\Microsoft\WinGet\Links";
    private const string LinkExe = @"C:\Users\u\AppData\Local\Microsoft\WinGet\Links\WindowsCareKit.exe";

    private static readonly Func<string, AppResourceKind, bool> NothingExists = (_, _) => false;

    [Fact]
    public void A_link_launch_produces_no_policy()
    {
        AppLayout layout = AppLayout.Create(
            processPath: LinkExe,
            resolvedTarget: AppExe,
            baseDirectory: LinkDir,
            NothingExists);

        Assert.False(layout.IsDetermined);
        Assert.Equal(LinkDir, layout.BaseDirectory);
        Assert.Throws<InvalidOperationException>(() => AppPayloadRoots.For(layout));
    }

    [Fact]
    public void Determined_layout_forbids_its_root()
    {
        AppLayout layout = AppLayout.Create(
            processPath: AppExe,
            resolvedTarget: null,
            baseDirectory: AppDir + Path.DirectorySeparatorChar,
            NothingExists);

        PayloadRootPolicy policy = AppPayloadRoots.For(layout);

        Assert.Equal(new[] { AppDir }, policy.ForbiddenRoots);
        Assert.False(string.Equals(
            Assert.Single(policy.ForbiddenRoots),
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_null_layout_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => AppPayloadRoots.For(null!));
    }
}

using WindowsCareKit.Core.Modules.Migration;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

/// <summary>F1: recipe paths expand ONLY via the closed KnownFolder enum + relative path; everything else is rejected.</summary>
public class RecipePathResolverTests
{
    private static RecipePathResolver R() => MigrationTestData.PathResolver();

    [Fact]
    public void Resolves_a_relative_path_under_the_known_folder()
    {
        string p = R().Resolve(KnownFolder.UserProfile, ".claude/settings.json");
        Assert.Equal(@"C:\Users\alice\.claude\settings.json", p);
    }

    [Fact]
    public void Resolves_appdata_and_localappdata_roots()
    {
        Assert.Equal(@"C:\Users\alice\AppData\Roaming\discord", R().Resolve(KnownFolder.AppData, "discord"));
        Assert.Equal(@"C:\Users\alice\AppData\Local\app", R().Resolve(KnownFolder.LocalAppData, "app"));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32")]   // absolute / rooted
    [InlineData(@"\\server\share\x")]      // UNC
    [InlineData("D:relative")]             // drive-qualified
    public void Rejects_rooted_unc_and_drive_qualified_paths(string bad)
        => Assert.Throws<RecipePathException>(() => R().Resolve(KnownFolder.UserProfile, bad));

    [Fact]
    public void Rejects_environment_token_in_path()
        => Assert.Throws<RecipePathException>(() => R().Resolve(KnownFolder.UserProfile, "%APPDATA%/x"));

    [Theory]
    [InlineData("../../Windows")]
    [InlineData(".claude/../../bob/secrets")]
    [InlineData("..")]
    public void Rejects_parent_traversal(string bad)
        => Assert.Throws<RecipePathException>(() => R().Resolve(KnownFolder.UserProfile, bad));

    [Fact]
    public void Rejects_empty_path()
        => Assert.Throws<RecipePathException>(() => R().Resolve(KnownFolder.UserProfile, ""));

    [Fact]
    public void RootFor_returns_the_normalized_profile_root()
        => Assert.Equal(@"C:\Users\alice", R().RootFor(KnownFolder.UserProfile));
}

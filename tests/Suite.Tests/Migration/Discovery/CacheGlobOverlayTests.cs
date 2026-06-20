using WindowsCareKit.Core.Modules.Migration.Discovery;
using Xunit;

namespace WindowsCareKit.Tests.Migration.Discovery;

/// <summary>
/// Verifies <see cref="CacheGlobOverlay.IsCacheLeaf"/> matches known junk names and rejects
/// ordinary app-data names. This is also the "handoff-exclude contract" guard test (F5): the
/// existence and coverage of these globs is the compile-time anchor PR-2 builds on.
/// </summary>
public class CacheGlobOverlayTests
{
    [Theory]
    [InlineData("node_modules")]
    [InlineData("NODE_MODULES")]         // case-insensitive
    [InlineData("blob_storage")]
    [InlineData("GPUCache")]
    [InlineData("Code Cache")]
    [InlineData("Cache")]
    [InlineData("Cache_Data")]
    [InlineData("Crashpad")]
    [InlineData("ShaderCache")]
    [InlineData("Service Worker")]
    [InlineData("CacheStorage")]
    [InlineData("SomethingCache")]       // *Cache* wildcard
    [InlineData("BrowserCache")]         // *Cache*
    [InlineData("HttpCache")]            // *Cache*
    public void Known_junk_names_are_matched(string leaf)
        => Assert.True(CacheGlobOverlay.IsCacheLeaf(leaf), $"'{leaf}' should be a cache leaf");

    [Theory]
    [InlineData("settings.json")]
    [InlineData("MyDocuments")]
    [InlineData("userdata")]
    [InlineData("profiles")]
    [InlineData("extensions")]
    [InlineData("Preferences")]
    [InlineData("logs")]        // explicitly dropped per F4
    [InlineData("temp")]        // explicitly dropped per F4
    [InlineData("Temp")]        // explicitly dropped per F4
    [InlineData("tmp")]         // explicitly dropped per F4
    public void Ordinary_names_are_not_matched(string leaf)
        => Assert.False(CacheGlobOverlay.IsCacheLeaf(leaf), $"'{leaf}' should NOT be a cache leaf");

    [Fact]
    public void Empty_string_is_not_matched()
        => Assert.False(CacheGlobOverlay.IsCacheLeaf(string.Empty));

    [Fact]
    public void Globs_list_is_non_empty_and_contains_known_anchor_names()
    {
        // PR-2 contract: the globs list must exist and contain the seeds that PR-2 injects.
        Assert.NotEmpty(CacheGlobOverlay.Globs);
        Assert.Contains("node_modules", CacheGlobOverlay.Globs);
        Assert.Contains("Cache", CacheGlobOverlay.Globs);
        Assert.Contains("*Cache*", CacheGlobOverlay.Globs);
    }
}

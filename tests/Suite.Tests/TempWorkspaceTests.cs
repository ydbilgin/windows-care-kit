using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Verifies the throwaway <see cref="TempWorkspace"/>: it lives under the temp path (never a real profile),
/// creates files with intermediate directories, and tears itself down on dispose.
/// </summary>
public class TempWorkspaceTests
{
    [Fact]
    public void Root_lives_under_the_temp_path()
    {
        using var ws = new TempWorkspace();
        Assert.StartsWith(
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(ws.Root),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(ws.Root));
    }

    [Fact]
    public void WriteFile_creates_intermediate_directories_and_content()
    {
        using var ws = new TempWorkspace();
        string p = ws.WriteFile("a/b/c.txt", "payload");

        Assert.True(File.Exists(p));
        Assert.Equal("payload", File.ReadAllText(p));
        Assert.Equal(ws.Combine("a", "b", "c.txt"), p);
    }

    [Fact]
    public void Dispose_removes_the_root()
    {
        string root;
        using (var ws = new TempWorkspace())
        {
            root = ws.Root;
            ws.WriteFile("x.txt", "1");
            Assert.True(Directory.Exists(root));
        }
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Two_workspaces_get_distinct_roots()
    {
        using var a = new TempWorkspace();
        using var b = new TempWorkspace();
        Assert.NotEqual(a.Root, b.Root);
    }
}

using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// <see cref="Win32AuthProbe"/> must answer existence ONLY — never reading file contents (spec §1.4).
/// These tests use a temp file/dir; they assert presence detection and env-var expansion.
/// </summary>
public class AuthProbeTests : IDisposable
{
    private readonly string _dir;
    private readonly Win32AuthProbe _probe = new();

    public AuthProbeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wck-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Detects_an_existing_file()
    {
        string file = Path.Combine(_dir, "auth.json");
        File.WriteAllText(file, "{\"token\":\"secret-never-read\"}");
        Assert.True(_probe.Exists(file));
    }

    [Fact]
    public void Detects_an_existing_directory()
        => Assert.True(_probe.Exists(_dir));

    [Fact]
    public void Missing_path_is_absent()
        => Assert.False(_probe.Exists(Path.Combine(_dir, "nope.json")));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_path_is_absent(string path)
        => Assert.False(_probe.Exists(path));

    [Fact]
    public void Expands_environment_variables_in_the_path()
    {
        // %TEMP% expands; the missing child does not exist → absent (proves expansion ran, not a literal match).
        string expanded = _probe.Exists("%TEMP%\\__wck_definitely_missing__")
            ? "found" : "absent";
        Assert.Equal("absent", expanded);
        // A real env-rooted path that exists resolves to present.
        Assert.True(_probe.Exists("%TEMP%"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }
}

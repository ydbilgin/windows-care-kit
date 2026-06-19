using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>Memory-only unit tests for the W1 port fakes (zero IO). These confirm the fakes are usable as
/// drop-in seams for later domain wiring (WF-B), independent of the disk.</summary>
public class AbstractionsFakesTests
{
    [Fact]
    public void FakeClock_returns_the_pinned_instant_and_is_settable()
    {
        var t0 = new DateTime(2026, 6, 19, 10, 0, 0, DateTimeKind.Utc);
        var clock = new FakeClock(t0);

        Assert.Equal(t0, clock.UtcNow);

        var t1 = t0.AddHours(2);
        clock.UtcNow = t1;
        Assert.Equal(t1, clock.UtcNow);
    }

    [Fact]
    public void SystemClock_is_in_utc()
    {
        Assert.Equal(DateTimeKind.Utc, new SystemClock().UtcNow.Kind);
    }

    [Fact]
    public void FakeHasher_returns_mapped_digest_and_a_deterministic_fallback()
    {
        var hasher = new FakeHasher().Map(@"C:\x\mapped.txt", "deadbeef");

        Assert.Equal("deadbeef", hasher.ComputeFileSha256(@"C:\x\mapped.txt"));

        // Unmapped → deterministic synthetic value; same input gives the same output.
        string unmapped = @"C:\x\other.txt";
        string first = hasher.ComputeFileSha256(unmapped);
        Assert.Equal(first, hasher.ComputeFileSha256(unmapped));
        Assert.Equal($"sha-{unmapped.Length}", first);
    }

    [Fact]
    public void FakeFileSystem_reports_existence_for_added_files_and_dirs()
    {
        var fs = new FakeFileSystem()
            .AddFile(@"C:\root\a.txt", "A")
            .AddDirectory(@"C:\root\empty");

        Assert.True(fs.FileExists(@"C:\root\a.txt"));
        Assert.False(fs.FileExists(@"C:\root\missing.txt"));
        Assert.True(fs.DirectoryExists(@"C:\root"));        // parent registered automatically
        Assert.True(fs.DirectoryExists(@"C:\root\empty"));
        Assert.False(fs.DirectoryExists(@"C:\nope"));
    }

    [Fact]
    public void FakeFileSystem_OpenRead_returns_the_stored_bytes()
    {
        var fs = new FakeFileSystem().AddFile(@"C:\root\a.txt", "hello");

        using var stream = fs.OpenRead(@"C:\root\a.txt");
        using var reader = new StreamReader(stream);
        Assert.Equal("hello", reader.ReadToEnd());
    }

    [Fact]
    public void FakeFileSystem_OpenRead_throws_for_a_missing_file()
    {
        var fs = new FakeFileSystem();
        Assert.Throws<FileNotFoundException>(() => fs.OpenRead(@"C:\root\ghost.txt"));
    }

    [Fact]
    public void FakeFileSystem_enumerates_top_level_vs_recursive()
    {
        var fs = new FakeFileSystem()
            .AddFile(@"C:\root\top.txt", "1")
            .AddFile(@"C:\root\sub\deep.txt", "2")
            .AddFile(@"C:\root\sub\nested\deeper.txt", "3");

        var top = fs.EnumerateFiles(@"C:\root", recursive: false).ToList();
        Assert.Equal(new[] { @"C:\root\top.txt" }, top);

        var all = fs.EnumerateFiles(@"C:\root", recursive: true).OrderBy(p => p).ToList();
        Assert.Equal(
            new[] { @"C:\root\sub\deep.txt", @"C:\root\sub\nested\deeper.txt", @"C:\root\top.txt" },
            all);
    }
}

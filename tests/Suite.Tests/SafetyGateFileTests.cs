using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

public class SafetyGateFileTests
{
    [Theory]
    [InlineData(@"C:\Program Files\SomeApp")]              // a real app folder under Program Files
    [InlineData(@"C:\Program Files\SomeApp\bin\app.exe")]
    [InlineData(@"C:\Users\alice\AppData\Local\App")]       // inside the profile, not the profile root
    [InlineData(@"D:\Games\Steam\leftover")]
    public void Allows_deleting_ordinary_app_paths(string path)
    {
        var verdict = TestData.Gate().Evaluate(TestData.FileDelete(path));
        Assert.True(verdict.Allowed, verdict.Reason);
    }

    [Theory]
    [InlineData(@"C:\")]                                   // drive root
    [InlineData(@"C:")]
    [InlineData(@"C:\Windows")]                            // exact protected dir
    [InlineData(@"C:\Windows\System32")]                   // under Windows
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\Program Files")]                      // exact protected dir
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\alice")]                        // profile root itself
    public void Blocks_deleting_protected_paths(string path)
    {
        var verdict = TestData.Gate().Evaluate(TestData.FileDelete(path));
        Assert.False(verdict.Allowed, "should be blocked: " + path);
    }

    [Fact]
    public void Blocks_junction_that_resolves_into_windows()
    {
        // The literal path looks harmless, but it is a reparse point pointing into System32.
        var canon = new FakeCanonicalizer()
            .Map(@"C:\harmless\link", @"C:\Windows\System32", reparse: true, resolved: true);

        var verdict = TestData.Gate(canon).Evaluate(TestData.FileDelete(@"C:\harmless\link"));

        Assert.False(verdict.Allowed);
        Assert.Contains("Windows", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Blocks_unresolvable_reparse_point()
    {
        var canon = new FakeCanonicalizer()
            .Map(@"C:\some\link", @"C:\some\link", reparse: true, resolved: false);

        var verdict = TestData.Gate(canon).Evaluate(TestData.FileDelete(@"C:\some\link"));

        Assert.False(verdict.Allowed);
        Assert.Contains("reparse", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Blocks_when_literal_is_safe_but_canonical_is_protected()
    {
        // Defense in depth: even if only the canonical path is dangerous, block.
        var canon = new FakeCanonicalizer()
            .Map(@"C:\Temp\x", @"C:\Program Files", reparse: false, resolved: true);

        var verdict = TestData.Gate(canon).Evaluate(TestData.FileDelete(@"C:\Temp\x"));

        Assert.False(verdict.Allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blocks_empty_path(string path)
    {
        var verdict = TestData.Gate().Evaluate(TestData.FileDelete(path));
        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void Blocks_unc_path()
    {
        var verdict = TestData.Gate().Evaluate(TestData.FileDelete(@"\\server\share\file"));
        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void Blocks_an_8_3_short_name_alias_that_expands_to_a_protected_dir()
    {
        // L12: the canonical resolution looks benign (identity), but the literal defense-in-depth branch
        // expands the 8.3 short name to C:\Program Files via the canonicalizer's ExpandLongPath → blocked.
        var canon = new FakeCanonicalizer()
            .MapLongPath(@"C:\PROGRA~1", @"C:\Program Files");

        var verdict = TestData.Gate(canon).Evaluate(TestData.FileDelete(@"C:\PROGRA~1"));

        Assert.False(verdict.Allowed);
    }
}

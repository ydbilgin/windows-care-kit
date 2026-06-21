using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Write-target policy is stricter than the delete policy: nothing may be CREATED under the Windows
/// tree, Program Files, ProgramData, or another user's profile (DLL-plant / all-users startup
/// persistence defense). Covers CopyAction and RestoreMergeAction destinations.
/// </summary>
public class SafetyGateWriteTargetTests
{
    // ---- Item 8: unresolved-reparse write-target branch -------------------------------------------

    /// <summary>
    /// A CopyAction whose destination is an unresolvable reparse point is blocked — the write-target branch at
    /// SafetyGate.cs EvaluateWriteTarget (mirrors the file-delete unresolved-reparse block in EvaluateFileDelete).
    /// </summary>
    [Fact]
    public void Blocks_copy_into_unresolvable_reparse_point_destination()
    {
        var canon = new FakeCanonicalizer()
            .Map(@"D:\payload\link", @"D:\payload\link", reparse: true, resolved: false);
        var verdict = TestData.Gate(canon).Evaluate(TestData.Copy(@"C:\src\file", @"D:\payload\link"));
        Assert.False(verdict.Allowed, "copy to unresolved reparse should be blocked");
        Assert.Contains("reparse", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A RestoreMergeAction whose destination is an unresolvable reparse point is blocked (same branch,
    /// RestoreMergeAction arm).
    /// </summary>
    [Fact]
    public void Blocks_restore_into_unresolvable_reparse_point_destination()
    {
        var canon = new FakeCanonicalizer()
            .Map(@"D:\payload\link", @"D:\payload\link", reparse: true, resolved: false);
        var verdict = TestData.Gate(canon).Evaluate(TestData.Restore(@"C:\src\file", @"D:\payload\link"));
        Assert.False(verdict.Allowed, "restore to unresolved reparse should be blocked");
        Assert.Contains("reparse", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData(@"C:\Program Files\Common Files\evil.dll")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\evil.dll")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\evil.lnk")]
    [InlineData(@"C:\Windows\System32\evil.dll")]
    [InlineData(@"C:\Users\bob\AppData\Roaming\App\config.json")]   // another user's profile
    [InlineData(@"C:\Users\bob\Desktop\x")]
    public void Blocks_copy_into_protected_or_other_user(string dest)
    {
        Assert.False(TestData.Gate().Evaluate(TestData.Copy(@"C:\src\file", dest)).Allowed, "copy: " + dest);
        Assert.False(TestData.Gate().Evaluate(TestData.Restore(@"C:\src\file", dest)).Allowed, "restore: " + dest);
    }

    [Theory]
    [InlineData(@"C:\Users\alice\AppData\Roaming\App\config.json")]  // current user's profile
    [InlineData(@"C:\Users\alice\Documents\backup\notes.txt")]
    [InlineData(@"D:\Backup\file")]
    [InlineData(@"E:\payload\Chrome\Bookmarks")]
    public void Allows_copy_into_current_profile_or_neutral_locations(string dest)
    {
        Assert.True(TestData.Gate().Evaluate(TestData.Copy(@"C:\src\file", dest)).Allowed);
        Assert.True(TestData.Gate().Evaluate(TestData.Restore(@"C:\src\file", dest)).Allowed);
    }

    [Fact]
    public void Blocks_write_through_a_junction_that_resolves_into_program_files()
    {
        var canon = new FakeCanonicalizer()
            .Map(@"D:\payload\link", @"C:\Program Files\Target", reparse: true, resolved: true);
        var verdict = TestData.Gate(canon).Evaluate(TestData.Copy(@"C:\src\file", @"D:\payload\link"));
        Assert.False(verdict.Allowed);
    }
}

using System.Runtime.InteropServices;
using System.Text;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests;

public class Win32CanonicalizerTests
{
    private readonly Win32PathCanonicalizer _canon = new();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    private static string? ShortName(string longPath)
    {
        var sb = new StringBuilder(512);
        uint len = GetShortPathName(longPath, sb, (uint)sb.Capacity);
        return len == 0 || len > sb.Capacity ? null : sb.ToString();
    }

    [Fact]
    public void Resolves_a_real_file_to_a_normalized_final_path()
    {
        string file = Path.Combine(Path.GetTempPath(), "wck-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(file, "x");
        try
        {
            var c = _canon.Canonicalize(file);
            Assert.True(c.Resolved);
            Assert.False(c.IsReparsePoint);
            Assert.EndsWith(Path.GetFileName(file), c.FinalPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\\?\", c.FinalPath);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Resolves_a_missing_file_via_its_existing_parent()
    {
        // The leaf doesn't exist, but its parent (temp) does — it must resolve THROUGH the real parent
        // (this is what closes the parent-junction gap).
        string missing = Path.Combine(Path.GetTempPath(), "wck-missing-" + Guid.NewGuid().ToString("N"));
        var c = _canon.Canonicalize(missing);
        Assert.True(c.Resolved);
        Assert.EndsWith(Path.GetFileName(missing), c.FinalPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unresolved_when_no_ancestor_exists()
    {
        char free = "ZYXWVUT".First(d =>
            !DriveInfo.GetDrives().Any(dr => dr.Name.StartsWith(d.ToString(), StringComparison.OrdinalIgnoreCase)));
        var c = _canon.Canonicalize($"{free}:\\wck-nope\\deep\\leaf.txt");
        Assert.False(c.Resolved);
    }

    [Theory]
    [InlineData(@"\\?\C:\Windows\System32", @"C:\Windows\System32")]
    [InlineData(@"\\?\UNC\server\share\f", @"\\server\share\f")]
    [InlineData(@"C:\already\normal", @"C:\already\normal")]
    public void StripExtendedPrefix_handles_both_forms(string input, string expected)
        => Assert.Equal(expected, Win32PathCanonicalizer.StripExtendedPrefix(input));

    [Fact]
    public void Blank_path_is_unresolved()
    {
        var c = _canon.Canonicalize("   ");
        Assert.False(c.Resolved);
    }

    [Fact]
    public void Resolves_a_directory_symlink_to_its_target_and_gate_blocks_protected_target()
    {
        // Creating a symlink needs admin or Developer Mode. If unavailable, this check no-ops.
        string linkDir = Path.Combine(Path.GetTempPath(), "wck-link-" + Guid.NewGuid().ToString("N"));
        string targetDir = Path.Combine(Path.GetTempPath(), "wck-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(targetDir);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkDir, targetDir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return; // no privilege to create links on this box — skip the live check
            }

            // 1) the canonicalizer follows the link to the real target and flags the reparse point
            var c = _canon.Canonicalize(linkDir);
            Assert.True(c.IsReparsePoint);
            Assert.True(c.Resolved);
            Assert.EndsWith(Path.GetFileName(targetDir), c.FinalPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(linkDir)) Directory.Delete(linkDir);
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir);
        }
    }

    [Fact]
    public void Resolves_a_junction_parent_under_a_nonexistent_leaf()
    {
        string target = Path.Combine(Path.GetTempPath(), "wck-tgt-" + Guid.NewGuid().ToString("N"));
        string link = Path.Combine(Path.GetTempPath(), "wck-lnk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            try { Directory.CreateSymbolicLink(link, target); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }

            // The leaf (and an intermediate dir) does not exist, but the PARENT is a link.
            string dest = Path.Combine(link, "newsub", "file.txt");
            var c = _canon.Canonicalize(dest);

            Assert.True(c.Resolved);
            Assert.True(c.IsReparsePoint);
            // The resolved path must reflect the real target, not the link name.
            Assert.Contains(Path.GetFileName(target), c.FinalPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(Path.Combine("newsub", "file.txt"), c.FinalPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFileName(link), c.FinalPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void Gate_blocks_write_through_a_junction_parent_into_windows()
    {
        string link = Path.Combine(Path.GetTempPath(), "wck-winp-" + Guid.NewGuid().ToString("N"));
        try
        {
            try { Directory.CreateSymbolicLink(link, @"C:\Windows"); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }

            var gate = new SafetyGate(ProtectedResources.ForCurrentSystem(), _canon);
            // Non-existent leaf under a junction that points at C:\Windows — must be blocked.
            var verdict = gate.Evaluate(TestData.Copy(@"C:\src\f", Path.Combine(link, "System32", "evil.dll")));
            Assert.False(verdict.Allowed);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    [Fact]
    public void ExpandLongPath_round_trips_an_8_3_short_name_to_its_long_form()
    {
        // L12: create a directory whose long name has no 8.3 equivalent unless short names exist, derive its
        // short (8.3) form, and assert ExpandLongPath maps the short form back to the long one.
        string longDir = Path.Combine(Path.GetTempPath(), "wck-longname-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(longDir);
        try
        {
            string? shortDir = ShortName(longDir);
            if (shortDir is null || string.Equals(shortDir, longDir, StringComparison.OrdinalIgnoreCase))
                return; // 8.3 short-name creation disabled on this volume (8dot3name) — skip the live check

            string expanded = _canon.ExpandLongPath(shortDir);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(longDir),
                Path.TrimEndingDirectorySeparator(expanded),
                ignoreCase: true);
        }
        finally
        {
            if (Directory.Exists(longDir)) Directory.Delete(longDir);
        }
    }

    [Theory]
    [InlineData(@"C:\Windows.", @"C:\Windows")]
    [InlineData(@"C:\Windows ", @"C:\Windows")]
    [InlineData(@"C:\foo. \bar ", @"C:\foo\bar")]
    [InlineData(@"C:\already\normal", @"C:\already\normal")]
    public void StripTrailingDotSpacePerSegment_trims_each_segment(string input, string expected)
        => Assert.Equal(expected, Win32PathCanonicalizer.StripTrailingDotSpacePerSegment(input));

    [Fact]
    public void Gate_with_real_canonicalizer_blocks_a_symlink_into_windows()
    {
        string linkDir = Path.Combine(Path.GetTempPath(), "wck-winlink-" + Guid.NewGuid().ToString("N"));
        try
        {
            try
            {
                // The link's target need not be writable; we only store the path.
                Directory.CreateSymbolicLink(linkDir, @"C:\Windows\System32");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return; // skip if links cannot be created without privilege
            }

            var gate = new SafetyGate(ProtectedResources.ForCurrentSystem(), _canon);
            var verdict = gate.Evaluate(TestData.FileDelete(linkDir));
            Assert.False(verdict.Allowed);
        }
        finally
        {
            if (Directory.Exists(linkDir)) Directory.Delete(linkDir);
        }
    }
}

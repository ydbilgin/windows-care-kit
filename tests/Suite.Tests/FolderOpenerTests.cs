using System.Diagnostics;
using System.IO;
using WindowsCareKit.Execution.Adapters;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// F4 / L5: <see cref="FolderOpener"/> refuses to launch Explorer when the canonicalizer reports an
/// unresolved reparse point OR a resolved final target that differs from the expected directory, and when it
/// does launch it pins Explorer to its absolute %WINDIR% path. The launch is captured through the internal
/// test seam so no real Explorer window is spawned.
/// </summary>
public class FolderOpenerTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "wck-fo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Launches_explorer_with_an_absolute_path_when_the_target_resolves_to_itself()
    {
        string dir = TempDir();
        try
        {
            string full = Path.GetFullPath(dir);
            ProcessStartInfo? launched = null;

            // The canonicalizer resolves the directory to itself (no redirection).
            var canon = new FakeCanonicalizer().Map(full, full, reparse: false, resolved: true);
            var opener = new FolderOpener(canon, psi => launched = psi);

            opener.OpenFolder(dir);

            Assert.NotNull(launched);
            // explorer.exe is pinned to an absolute %WINDIR% path (no bare image name).
            Assert.True(Path.IsPathFullyQualified(launched!.FileName));
            Assert.EndsWith("explorer.exe", launched.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.False(launched.UseShellExecute);
            // The resolved final path is passed as a discrete argument (never a shell string).
            Assert.Equal(full, Assert.Single(launched.ArgumentList));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Refuses_when_the_target_is_an_unresolved_reparse_point()
    {
        string dir = TempDir();
        try
        {
            string full = Path.GetFullPath(dir);
            bool launched = false;

            // A reparse point the canonicalizer could not resolve → untrustworthy → no-op.
            var canon = new FakeCanonicalizer().Map(full, full, reparse: true, resolved: false);
            var opener = new FolderOpener(canon, _ => launched = true);

            opener.OpenFolder(dir);

            Assert.False(launched);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Refuses_when_the_resolved_final_path_differs_from_the_expected_directory()
    {
        string dir = TempDir();
        try
        {
            string full = Path.GetFullPath(dir);
            bool launched = false;

            // The canonicalizer resolves to a DIFFERENT directory (a junction/symlink redirect) → no-op.
            var canon = new FakeCanonicalizer().Map(full, @"C:\Windows\System32", reparse: true, resolved: true);
            var opener = new FolderOpener(canon, _ => launched = true);

            opener.OpenFolder(dir);

            Assert.False(launched);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void NoOps_when_the_path_does_not_exist()
    {
        bool launched = false;
        // A non-existent path never reaches canonicalization or launch.
        var opener = new FolderOpener(new FakeCanonicalizer(), _ => launched = true);

        opener.OpenFolder(Path.Combine(Path.GetTempPath(), "wck-fo-missing-" + Guid.NewGuid().ToString("N")));

        Assert.False(launched);
    }

    [Fact]
    public void NoOps_on_a_null_or_whitespace_path()
    {
        bool launched = false;
        var opener = new FolderOpener(new FakeCanonicalizer(), _ => launched = true);

        opener.OpenFolder("");
        opener.OpenFolder("   ");

        Assert.False(launched);
    }
}

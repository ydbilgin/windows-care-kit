using System.Diagnostics;

namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// Shared NTFS directory-junction helpers for real-FS reparse-point tests. Extracted verbatim from the
/// private members of <c>CopyAdapterTests</c> so the junction-boundary tests in both that file and the
/// Step 4 real-gate E2E suite use one implementation (no duplication). A junction needs no elevation, but
/// some environments still disallow it; callers treat a false return as "skip this case".
/// </summary>
internal static class JunctionHelper
{
    /// <summary>Create an NTFS directory junction (no elevation needed) via cmd's mklink; false if unavailable.</summary>
    public static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process? p = Process.Start(psi);
            if (p is null)
                return false;
            p.WaitForExit(10_000);
            // Confirm the link exists AND is actually a reparse point.
            return Directory.Exists(link)
                   && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tear down a temp root that contains a junction: unlink the junction itself FIRST (a non-recursive
    /// delete removes the reparse point without recursing into / deleting the real target), then recursively
    /// delete the root. A plain recursive delete of the root would otherwise fail on the junction.
    /// </summary>
    public static void CleanupWithJunction(string root, string junction)
    {
        try
        {
            if (Directory.Exists(junction) && File.GetAttributes(junction).HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(junction, recursive: false); // unlink the reparse point only
        }
        catch { /* best-effort teardown */ }
        if (Directory.Exists(root))
            TestFs.DeleteResilient(root);
    }
}

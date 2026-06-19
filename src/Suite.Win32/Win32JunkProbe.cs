using System.IO;
using WindowsCareKit.Core.Modules.Clean;

namespace WindowsCareKit.Win32;

/// <summary>
/// Read-only junk/temp folder finder. It reports the user/Windows temp folders and the Chromium-family
/// browser cache folders as deletion candidates. It only enumerates and sizes folders — it never
/// deletes (deletion is a gated recycle-bin <c>FileDeleteAction</c> emitted by <see cref="JunkScanner"/>).
/// Sizing is best-effort and bounded so a huge tree never stalls the UI.
/// </summary>
public sealed class Win32JunkProbe : IJunkProbe
{
    /// <summary>Cap the directory walk so an enormous cache cannot stall the scan.</summary>
    private const int MaxFilesPerCandidate = 50_000;

    public IReadOnlyList<JunkCandidate> FindJunk()
    {
        var found = new List<JunkCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? path, string note)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }
            if (!seen.Add(full))
                return;
            if (!Directory.Exists(full))
                return;
            found.Add(new JunkCandidate(full, ApproxSize(full), note));
        }

        // User + Windows temp folders.
        Consider(Path.GetTempPath().TrimEnd('\\'), "User temp folder");
        Consider(Environment.GetEnvironmentVariable("TEMP"), "TEMP folder");
        Consider(Environment.GetEnvironmentVariable("TMP"), "TMP folder");
        string winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        Consider(winTemp, "Windows temp folder");

        // Chromium-family browser caches under %LocalAppData%.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach ((string vendor, string label) in BrowserCacheVendors())
        {
            string userData = Path.Combine(localAppData, vendor, "User Data");
            if (!Directory.Exists(userData))
                continue;
            foreach (string profile in EnumerateProfiles(userData))
            {
                Consider(Path.Combine(profile, "Cache"), $"{label} cache");
                Consider(Path.Combine(profile, "Code Cache"), $"{label} code cache");
                Consider(Path.Combine(profile, "GPUCache"), $"{label} GPU cache");
            }
        }

        return found;
    }

    /// <summary>Chromium-family vendor folder names under %LocalAppData% and their UI labels.</summary>
    private static IEnumerable<(string Vendor, string Label)> BrowserCacheVendors()
    {
        yield return (@"Google\Chrome", "Chrome");
        yield return (@"Microsoft\Edge", "Edge");
        yield return (@"BraveSoftware\Brave-Browser", "Brave");
        yield return (@"Vivaldi", "Vivaldi");
        yield return (@"Opera Software\Opera Stable", "Opera");
    }

    /// <summary>Profile subfolders (Default, Profile 1, …) plus the user-data root for top-level caches.</summary>
    private static IEnumerable<string> EnumerateProfiles(string userData)
    {
        yield return userData;
        string[] dirs;
        try { dirs = Directory.GetDirectories(userData); }
        catch { yield break; }
        foreach (string d in dirs)
        {
            string name = Path.GetFileName(d);
            if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                yield return d;
        }
    }

    /// <summary>Best-effort bounded size of a directory tree (returns 0 on any access error).</summary>
    internal static long ApproxSize(string dir)
    {
        long total = 0;
        int count = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint, // do not follow junctions/symlinks
            };
            foreach (string file in Directory.EnumerateFiles(dir, "*", options))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* a file we cannot stat is skipped */ }
                if (++count >= MaxFilesPerCandidate)
                    break;
            }
        }
        catch
        {
            return total;
        }
        return total;
    }
}

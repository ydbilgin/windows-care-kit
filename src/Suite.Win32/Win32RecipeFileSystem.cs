using System.IO;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Discovery;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Win32;

/// <summary>
/// Production <see cref="IRecipeFileSystem"/> and <see cref="IDiscoveryFileSystem"/> for the migration
/// backup READ path: ordinary System.IO existence/reparse checks plus reparse-following canonicalization
/// via <see cref="Win32PathCanonicalizer"/> (GetFinalPathNameByHandle). This is what makes
/// <see cref="RecipeResolver"/>'s profile-root containment actually defeat junction/symlink escapes on
/// the real disk (the test fake stands in for it host-safe).
///
/// Read-only by construction: a recipe is declarative and the backup direction only reads the source.
/// </summary>
public sealed class Win32RecipeFileSystem : IRecipeFileSystem, IDiscoveryFileSystem
{
    private readonly Win32PathCanonicalizer _canon = new();

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch { return false; }
    }

    /// <summary>
    /// Resolve to the true on-disk target (following junctions/symlinks). Returns null when no real handle
    /// could be opened — an UNRESOLVED reparse point must fail closed, never pass the sandbox. The returned
    /// path has the <c>\\?\</c> prefix stripped (by the canonicalizer) so it compares against the profile root.
    /// </summary>
    public string? Canonicalize(string path)
    {
        CanonicalPath c = _canon.Canonicalize(path);
        if (!c.Resolved || string.IsNullOrEmpty(c.FinalPath))
            return null;
        return Path.TrimEndingDirectorySeparator(c.FinalPath);
    }

    /// <summary>
    /// Returns direct children of <paramref name="directory"/> (non-recursive). Reparse-point entries ARE
    /// returned (with their <see cref="FileAttributes.ReparsePoint"/> flag) — the discovery engine must SEE
    /// them to surface a junction-relocated app (<c>NotTraversedReparse</c>, spec F3) and to skip reparse
    /// children itself; skipping them here would silently omit relocated apps. Inaccessible entries are
    /// silently skipped (<c>IgnoreInaccessible</c>). Per-entry metadata (attributes, last-write time) is
    /// best-effort — entries that throw on metadata access are skipped. The discovery engine imposes a
    /// deterministic order over these entries (spec F2), so no ordering guarantee is made here.
    /// </summary>
    public IEnumerable<DiscoveryFileSystemEntry> EnumerateChildren(string directory)
    {
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
        };

        foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", opts))
        {
            FileAttributes attrs;
            DateTime lastWrite;
            bool isDir;
            try
            {
                attrs = File.GetAttributes(path);
                lastWrite = File.GetLastWriteTimeUtc(path);
                isDir = attrs.HasFlag(FileAttributes.Directory);
            }
            catch
            {
                continue; // metadata inaccessible — skip
            }

            yield return new DiscoveryFileSystemEntry(path, isDir, attrs, lastWrite);
        }
    }
}

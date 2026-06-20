using System.IO;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Win32;

/// <summary>
/// Production <see cref="IRecipeFileSystem"/> for the migration backup READ path: ordinary System.IO
/// existence/reparse checks plus reparse-following canonicalization via <see cref="Win32PathCanonicalizer"/>
/// (GetFinalPathNameByHandle). This is what makes <see cref="RecipeResolver"/>'s profile-root containment
/// actually defeat junction/symlink escapes on the real disk (the test fake stands in for it host-safe).
///
/// Read-only by construction: a recipe is declarative and the backup direction only reads the source.
/// </summary>
public sealed class Win32RecipeFileSystem : IRecipeFileSystem
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
}

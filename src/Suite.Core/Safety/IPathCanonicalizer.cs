namespace WindowsCareKit.Core.Safety;

/// <summary>
/// The real, resolved location of a path. <see cref="FinalPath"/> follows junctions and symlinks to
/// the actual target — this is what closes the bypass where a reparse point like
/// <c>C:\Users\All Users</c> → <c>C:\ProgramData</c> could be used to delete a protected directory
/// through an innocent-looking path (spec §3).
/// </summary>
/// <param name="Original">The path as supplied by the caller.</param>
/// <param name="FinalPath">The canonical target after resolving reparse points; falls back to a
/// normalized full path when the target cannot be opened (e.g. it does not exist).</param>
/// <param name="IsReparsePoint">True when the original path (or a component) is a junction/symlink.</param>
/// <param name="Resolved">True when a real OS handle was opened to resolve the final path.</param>
public sealed record CanonicalPath(string Original, string FinalPath, bool IsReparsePoint, bool Resolved);

/// <summary>
/// Resolves a filesystem path to its true canonical target. Implemented in Suite.Win32 via
/// <c>GetFinalPathNameByHandle</c> + reparse-attribute checks; <c>Path.GetFullPath</c> alone is not
/// enough because it does not follow junctions/symlinks (spec §3, §4).
/// </summary>
public interface IPathCanonicalizer
{
    CanonicalPath Canonicalize(string path);

    /// <summary>
    /// Normalizes a path's LITERAL form: full-qualifies it, expands any 8.3 short components to their long
    /// names (<c>GetLongPathNameW</c>), and strips trailing dots/spaces per segment. Unlike
    /// <see cref="Canonicalize"/> this does NOT follow reparse points — it hardens the literal
    /// defense-in-depth branch of the gate against short-name / trailing-dot aliases of a protected path
    /// (spec §3, §4; L12). Returns a best-effort normalized full path (never throws on a malformed input).
    /// </summary>
    string ExpandLongPath(string path);
}

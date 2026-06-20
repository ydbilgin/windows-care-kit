namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// The minimal, read-only file-system surface <see cref="RecipeResolver"/> needs to run its on-disk sandbox
/// (exists? is-it-a-reparse-point? what-does-it-canonicalize-to?). Injected so the resolver — and therefore
/// every sandbox-escape test — runs host-safe against an in-memory fake (decision §SLICE 1 "fake-FS").
///
/// It is read-only by construction: a recipe is declarative and the backup direction only READS the source.
/// </summary>
public interface IRecipeFileSystem
{
    /// <summary>True when a directory exists at <paramref name="path"/>.</summary>
    bool DirectoryExists(string path);

    /// <summary>True when a file exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);

    /// <summary>True when the path (file or directory) is a junction/symlink/reparse point.</summary>
    bool IsReparsePoint(string path);

    /// <summary>
    /// Canonicalize <paramref name="path"/> to its true on-disk target (following junctions/symlinks).
    /// Returns null when the path could not be resolved to a trustworthy real target — the resolver then
    /// fails closed (an unresolved reparse point must never pass the sandbox).
    /// </summary>
    string? Canonicalize(string path);
}

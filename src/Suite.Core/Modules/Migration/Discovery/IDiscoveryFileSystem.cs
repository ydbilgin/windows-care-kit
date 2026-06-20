namespace WindowsCareKit.Core.Modules.Migration.Discovery;

/// <summary>A single file-system entry returned by <see cref="IDiscoveryFileSystem.EnumerateChildren"/>.</summary>
/// <param name="Path">Absolute path to the entry.</param>
/// <param name="IsDirectory">True when the entry is a directory.</param>
/// <param name="Attributes">Raw <see cref="System.IO.FileAttributes"/> (best-effort; may default on access error).</param>
/// <param name="LastWriteTimeUtc">Last write time in UTC (best-effort; may be default on access error).</param>
public sealed record DiscoveryFileSystemEntry(
    string Path,
    bool IsDirectory,
    System.IO.FileAttributes Attributes,
    DateTime LastWriteTimeUtc);

/// <summary>
/// File-system surface for policy-owned discovery walks. Extends <see cref="IRecipeFileSystem"/> so the production
/// <see cref="WindowsCareKit.Win32.Win32RecipeFileSystem"/> needs only one additional method.
///
/// <para>The walker calls <see cref="EnumerateChildren"/> at each directory level and decides itself whether to
/// descend, prune, or skip — the non-recursive shape is what lets Core express the per-directory cache/secret/
/// reparse prune policy that a recursive BCL enumeration cannot.</para>
/// </summary>
public interface IDiscoveryFileSystem : IRecipeFileSystem
{
    /// <summary>
    /// Returns the direct children of <paramref name="directory"/> (non-recursive). The implementation
    /// uses <c>EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }</c>;
    /// per-entry metadata is best-effort — entries that throw on attribute/time access are skipped.
    /// </summary>
    IEnumerable<DiscoveryFileSystemEntry> EnumerateChildren(string directory);
}

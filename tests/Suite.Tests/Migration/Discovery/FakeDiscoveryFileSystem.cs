using System.IO;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Discovery;

namespace WindowsCareKit.Tests.Migration.Discovery;

/// <summary>
/// In-memory <see cref="IDiscoveryFileSystem"/> for deterministic, host-safe discovery tests.
/// Extends the <see cref="FakeRecipeFileSystem"/> shape: adds enumerable child entries with timestamps,
/// attributes, and per-path access-error simulation. Reparse-point handling mirrors the base fake.
/// </summary>
internal sealed class FakeDiscoveryFileSystem : IDiscoveryFileSystem
{
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    // path -> canonical target (null = unresolved reparse point)
    private readonly Dictionary<string, string?> _reparse = new(StringComparer.OrdinalIgnoreCase);

    // parent path -> ordered list of child entries
    private readonly Dictionary<string, List<DiscoveryFileSystemEntry>> _children
        = new(StringComparer.OrdinalIgnoreCase);

    // paths that throw on EnumerateChildren
    private readonly HashSet<string> _throwOnEnumerate = new(StringComparer.OrdinalIgnoreCase);

    // ── IRecipeFileSystem ───────────────────────────────────────────────────────────────────────

    public bool DirectoryExists(string path) => _dirs.Contains(Norm(path));
    public bool FileExists(string path) => _files.Contains(Norm(path));
    public bool IsReparsePoint(string path) => _reparse.ContainsKey(Norm(path));

    public string? Canonicalize(string path)
    {
        string p = Norm(path);
        if (_reparse.TryGetValue(p, out string? target))
            return target; // may be null = unresolved
        return p;          // ordinary path canonicalizes to itself
    }

    // ── IDiscoveryFileSystem ────────────────────────────────────────────────────────────────────

    public IEnumerable<DiscoveryFileSystemEntry> EnumerateChildren(string directory)
    {
        string p = Norm(directory);
        if (_throwOnEnumerate.Contains(p))
            throw new UnauthorizedAccessException($"Fake: access denied to {p}");

        if (_children.TryGetValue(p, out List<DiscoveryFileSystemEntry>? list))
            return list;

        return Array.Empty<DiscoveryFileSystemEntry>();
    }

    // ── Builder ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Add a directory (no children yet).</summary>
    public FakeDiscoveryFileSystem AddDir(string path)
    {
        _dirs.Add(Norm(path));
        return this;
    }

    /// <summary>Add a file (no children yet — files are leaves).</summary>
    public FakeDiscoveryFileSystem AddFile(string path)
    {
        _files.Add(Norm(path));
        return this;
    }

    /// <summary>
    /// Add a reparse-point path with a canonical target (null = unresolved). Also marks the path as
    /// existing (dir by default).
    /// </summary>
    public FakeDiscoveryFileSystem AddReparse(string path, string? target, bool asDir = true)
    {
        string p = Norm(path);
        _reparse[p] = target is null ? null : Norm(target);
        if (asDir) _dirs.Add(p); else _files.Add(p);
        return this;
    }

    /// <summary>
    /// Append a DIRECTORY child under <paramref name="parentDir"/>. The child is also registered as a
    /// known directory. Pass <paramref name="isReparse"/>=true to mark it a reparse point (attributes
    /// will include <see cref="FileAttributes.ReparsePoint"/>).
    /// </summary>
    public FakeDiscoveryFileSystem AddChildDir(
        string parentDir, string childName,
        DateTime? lastWrite = null, bool isReparse = false)
    {
        string parent = Norm(parentDir);
        string childPath = Path.Combine(parent, childName);
        FileAttributes attrs = FileAttributes.Directory;
        if (isReparse)
        {
            attrs |= FileAttributes.ReparsePoint;
            _reparse[childPath] = null; // unresolved by default for reparse children
        }
        _dirs.Add(childPath);
        GetOrAddChildren(parent).Add(new DiscoveryFileSystemEntry(
            childPath, IsDirectory: true, attrs, lastWrite ?? DateTime.MinValue));
        return this;
    }

    /// <summary>
    /// Append a FILE child under <paramref name="parentDir"/>. The child is also registered as a known file.
    /// </summary>
    public FakeDiscoveryFileSystem AddChildFile(
        string parentDir, string fileName, DateTime lastWrite)
    {
        string parent = Norm(parentDir);
        string filePath = Path.Combine(parent, fileName);
        _files.Add(filePath);
        GetOrAddChildren(parent).Add(new DiscoveryFileSystemEntry(
            filePath, IsDirectory: false, FileAttributes.Normal, lastWrite));
        return this;
    }

    /// <summary>Mark <paramref name="dir"/> so that <see cref="EnumerateChildren"/> throws.</summary>
    public FakeDiscoveryFileSystem SetThrowOnEnumerate(string dir)
    {
        _throwOnEnumerate.Add(Norm(dir));
        return this;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private List<DiscoveryFileSystemEntry> GetOrAddChildren(string normParent)
    {
        if (!_children.TryGetValue(normParent, out List<DiscoveryFileSystemEntry>? list))
        {
            list = new List<DiscoveryFileSystemEntry>();
            _children[normParent] = list;
        }
        return list;
    }

    private static string Norm(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

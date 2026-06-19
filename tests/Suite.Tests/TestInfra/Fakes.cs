using WindowsCareKit.Core.Abstractions;

namespace WindowsCareKit.Tests.TestInfra;

/// <summary>A clock pinned to a fixed instant, so timestamp-dependent logic is deterministic in tests.</summary>
internal sealed class FakeClock : IClock
{
    public FakeClock(DateTime utcNow) => UtcNow = utcNow;

    public DateTime UtcNow { get; set; }
}

/// <summary>
/// An in-memory hasher. Explicitly mapped paths return their configured digest; unmapped paths fall back to
/// a deterministic synthetic value derived from the path, so tests never need to touch the disk.
/// </summary>
internal sealed class FakeHasher : IHasher
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pin <paramref name="path"/> to return <paramref name="sha256"/>.</summary>
    public FakeHasher Map(string path, string sha256)
    {
        _map[path] = sha256;
        return this;
    }

    public string ComputeFileSha256(string path)
        => _map.TryGetValue(path, out var sha) ? sha : $"sha-{path.Length}";
}

/// <summary>
/// An in-memory <see cref="IFileSystem"/> backed by simple dictionaries: zero real IO. Files map a full path
/// to its bytes; directories are tracked explicitly. <see cref="EnumerateFiles"/> matches on a path-prefix.
/// </summary>
internal sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Add a file (and register its parent directory chain).</summary>
    public FakeFileSystem AddFile(string path, byte[] content)
    {
        _files[path] = content;
        string? dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            _dirs.Add(dir);
            dir = Path.GetDirectoryName(dir);
        }
        return this;
    }

    /// <summary>Add a file from a UTF-8 string body.</summary>
    public FakeFileSystem AddFile(string path, string content)
        => AddFile(path, System.Text.Encoding.UTF8.GetBytes(content));

    /// <summary>Register a directory with no files.</summary>
    public FakeFileSystem AddDirectory(string path)
    {
        _dirs.Add(path);
        return this;
    }

    public bool FileExists(string path) => _files.ContainsKey(path);

    public bool DirectoryExists(string path) => _dirs.Contains(path);

    public System.IO.Stream OpenRead(string path)
        => _files.TryGetValue(path, out var bytes)
            ? new MemoryStream(bytes, writable: false)
            : throw new FileNotFoundException("No such fake file.", path);

    public IEnumerable<string> EnumerateFiles(string root, bool recursive)
    {
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        foreach (var path in _files.Keys)
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (recursive)
            {
                yield return path;
            }
            else
            {
                // Top-level only: no further separator after the prefix.
                string remainder = path.Substring(prefix.Length);
                if (!remainder.Contains(Path.DirectorySeparatorChar))
                    yield return path;
            }
        }
    }
}

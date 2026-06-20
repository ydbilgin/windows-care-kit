using WindowsCareKit.Core.Modules.Migration;

namespace WindowsCareKit.Tests.Migration;

/// <summary>
/// An in-memory <see cref="IRecipeFileSystem"/> so every Migration sandbox test runs host-safe (no real
/// disk, no real junctions). Directories/files are declared explicitly; reparse points map a source path to
/// either a canonical target (resolved junction) or null (unresolved → fail-closed).
/// </summary>
internal sealed class FakeRecipeFileSystem : IRecipeFileSystem
{
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    // path -> canonical target (null entry = unresolved reparse point)
    private readonly Dictionary<string, string?> _reparse = new(StringComparer.OrdinalIgnoreCase);

    public FakeRecipeFileSystem AddDir(string path) { _dirs.Add(Norm(path)); return this; }
    public FakeRecipeFileSystem AddFile(string path) { _files.Add(Norm(path)); return this; }

    /// <summary>Mark <paramref name="path"/> a reparse point that canonicalizes to <paramref name="target"/>
    /// (use null for an unresolved reparse point). The path is also treated as existing.</summary>
    public FakeRecipeFileSystem AddReparse(string path, string? target, bool asDir = true)
    {
        string p = Norm(path);
        _reparse[p] = target is null ? null : Norm(target);
        if (asDir) _dirs.Add(p); else _files.Add(p);
        return this;
    }

    public bool DirectoryExists(string path) => _dirs.Contains(Norm(path));
    public bool FileExists(string path) => _files.Contains(Norm(path));
    public bool IsReparsePoint(string path) => _reparse.ContainsKey(Norm(path));

    public string? Canonicalize(string path)
    {
        string p = Norm(path);
        if (_reparse.TryGetValue(p, out string? target))
            return target; // may be null = unresolved
        return p;           // ordinary path canonicalizes to itself
    }

    private static string Norm(string path)
        => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
}

/// <summary>Shared fixtures for the Migration tests: a fixed set of profile roots + a path resolver.</summary>
internal static class MigrationTestData
{
    public static ProfileRoots Roots() => new(
        UserProfile: @"C:\Users\alice",
        AppData: @"C:\Users\alice\AppData\Roaming",
        LocalAppData: @"C:\Users\alice\AppData\Local");

    public static RecipePathResolver PathResolver() => new(Roots());

    public static RecipeResolver Resolver(IRecipeFileSystem fs) => new(PathResolver(), fs);
}

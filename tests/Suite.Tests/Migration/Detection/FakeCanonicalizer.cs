using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Tests.Migration.Detection;

/// <summary>
/// Identity/echo <see cref="IPathCanonicalizer"/> for Detection unit tests. Canonicalize returns
/// the input unchanged (non-reparse, resolved). ExpandLongPath also echoes the input.
/// </summary>
internal sealed class FakeCanonicalizer : IPathCanonicalizer
{
    public static readonly FakeCanonicalizer Instance = new();

    public CanonicalPath Canonicalize(string path)
        => new(Original: path, FinalPath: path, IsReparsePoint: false, Resolved: true);

    public string ExpandLongPath(string path) => path;
}

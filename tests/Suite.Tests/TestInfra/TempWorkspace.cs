namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// A throwaway working directory under <see cref="Path.GetTempPath"/> for real-IO tests. Created in the
/// ctor under a unique <see cref="Guid"/>-named root; best-effort recursively deleted on <see cref="Dispose"/>.
/// NEVER points at %USERPROFILE%/AppData/a real profile — only the per-user temp area.
/// </summary>
/// <remarks>
/// Usage: <c>using var ws = new TempWorkspace(); var p = ws.WriteFile("a/b.txt", "hi");</c>
/// </remarks>
internal sealed class TempWorkspace : IDisposable
{
    /// <summary>The absolute root of this workspace (a fresh directory under the temp path).</summary>
    public string Root { get; }

    public TempWorkspace(string? prefix = null)
    {
        string name = (prefix ?? "wck-test-") + Guid.NewGuid().ToString("N");
        Root = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(Root);
    }

    /// <summary>Combine path segments relative to <see cref="Root"/>.</summary>
    public string Combine(params string[] relativeSegments)
        => Path.Combine(new[] { Root }.Concat(relativeSegments).ToArray());

    /// <summary>
    /// Write <paramref name="content"/> to a file at <paramref name="relativePath"/> under the workspace,
    /// creating any intermediate directories. Returns the absolute file path written.
    /// </summary>
    public string WriteFile(string relativePath, string content)
    {
        // Normalize forward slashes so callers may use "a/b/c.txt" and get a canonical platform path back.
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string full = Path.Combine(Root, normalized);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Best-effort recursive teardown; never throws (the OS reclaims temp on reboot regardless).</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                TestFs.DeleteResilient(Root);
        }
        catch
        {
            // Best-effort: a locked handle must not fail an otherwise-passing test.
        }
    }
}

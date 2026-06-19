using System.Diagnostics;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Opens an existing directory in Explorer. This is a benign, read-only UI affordance — not a gated
/// destructive action — so it does not go through the executor. It validates that the target is an
/// existing directory and passes it via <see cref="ProcessStartInfo.ArgumentList"/> (never a shell
/// string), so an attacker-shaped argument cannot turn it into arbitrary command execution.
///
/// <para>L5 hardening: the path is also canonicalized via <see cref="IPathCanonicalizer"/>. If it is an
/// unresolved reparse point, or the resolved final target does not match the expected full path, the
/// open is refused — so a junction/symlink cannot redirect the user into an unexpected location.</para>
/// </summary>
public sealed class FolderOpener : IFolderOpener
{
    private readonly IPathCanonicalizer _canonicalizer;
    private readonly Action<ProcessStartInfo> _launch;

    public FolderOpener(IPathCanonicalizer canonicalizer)
#pragma warning disable RS0030 // Sanctioned process launch (Suite.Execution): open Explorer for a validated folder.
        : this(canonicalizer, psi => Process.Start(psi))
#pragma warning restore RS0030
    {
    }

    /// <summary>Test seam: inject the launch action so the L5 accept/refuse decision can be verified without
    /// actually spawning Explorer. Production always uses the public ctor (real <see cref="Process.Start"/>).</summary>
    internal FolderOpener(IPathCanonicalizer canonicalizer, Action<ProcessStartInfo> launch)
    {
        _canonicalizer = canonicalizer ?? throw new ArgumentNullException(nameof(canonicalizer));
        _launch = launch ?? throw new ArgumentNullException(nameof(launch));
    }

    /// <inheritdoc />
    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        string fullPath = Path.GetFullPath(path);

        CanonicalPath canon = _canonicalizer.Canonicalize(fullPath);

        // An unresolved reparse point is untrustworthy → refuse.
        if (canon.IsReparsePoint && !canon.Resolved)
            return;

        // The resolved target must be exactly the directory we expect — a junction/symlink that redirects
        // elsewhere (FinalPath differs from the full path) is refused.
        if (!PathsEqual(canon.FinalPath, fullPath))
            return;

        // Pin Explorer to its absolute System path (no bare image name → no PATH / App-Paths resolution),
        // matching the gate's own no-bare-name discipline for command actions.
        string explorer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        var psi = new ProcessStartInfo
        {
            FileName = explorer,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(canon.FinalPath);
        _launch(psi);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            StringComparison.OrdinalIgnoreCase);
}

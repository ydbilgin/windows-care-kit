using System.Text.RegularExpressions;
using WindowsCareKit.Core.Modules.Uninstall;

namespace WindowsCareKit.Core.Safety;

/// <summary>
/// Command-policy Phase 2 helpers shared by the GATE (the authoritative decision point,
/// <see cref="SafetyGate"/>) and the PLANNER (the pre-flight, <c>OfficialUninstallerPlanner</c>). Centralizing
/// the narrow carve-outs here means the planner can never produce an action the gate would block, and never
/// reject one the gate would allow — one implementation, not a divergent copy.
///
/// Everything here is ADDITIVE: it can only decide whether an already-Phase-1-clean elevated official
/// uninstaller may run; it never re-admits a command the Phase-1 checks denied.
/// </summary>
internal static class CommandPolicy
{
    // NSIS copies its uninstaller to a RANDOMIZED %TEMP%\~nsu<HEX>.tmp\Au_.exe and re-launches it. The carve-out
    // pins BOTH halves — the ~nsu<HEX>.tmp directory shape AND the Au_.exe leaf, with NO deeper nesting — so it is
    // never a dir-only writable-temp exec hole (Fix 2 + Fix 3). A literal "~nsu.tmp" would be WRONG: it would
    // block real uninstallers (the dir is randomized) and would also be too loose if used as a prefix.
    private const string NsisDirPattern = @"^~nsu[0-9A-Fa-f]*\.tmp$";
    private const string NsisLeaf = "Au_.exe";

    private static readonly Regex NsisDirRegex = new(
        NsisDirPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// True when <paramref name="expandedPath"/> is exactly <c>%TEMP%\~nsu&lt;HEX&gt;.tmp\Au_.exe</c> — the NSIS
    /// uninstaller carve-out (Fix 2 + Fix 3). The parent directory's leaf must match the <c>~nsu&lt;HEX&gt;.tmp</c>
    /// shape AND that directory must sit DIRECTLY under %TEMP% (no deeper nesting), AND the file leaf must be
    /// exactly <c>Au_.exe</c>. Both halves are pinned; dir-only would be a writable-temp exec hole. Comparison is
    /// on canonicalized full paths so 8.3 / casing differences of %TEMP% do not defeat it.
    /// </summary>
    internal static bool IsNsisUninstaller(string expandedPath)
    {
        if (string.IsNullOrWhiteSpace(expandedPath))
            return false;

        string leaf;
        string? dir;
        try
        {
            string full = System.IO.Path.GetFullPath(expandedPath.Trim().TrimEnd('.', ' '));
            leaf = System.IO.Path.GetFileName(full);
            dir = System.IO.Path.GetDirectoryName(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (dir is null || !string.Equals(leaf, NsisLeaf, StringComparison.OrdinalIgnoreCase))
            return false;

        string dirLeaf = System.IO.Path.GetFileName(dir);
        if (!NsisDirRegex.IsMatch(dirLeaf))
            return false;

        // The ~nsu<HEX>.tmp directory must be DIRECTLY under %TEMP% (no deeper nesting).
        string? grandparent = System.IO.Path.GetDirectoryName(dir);
        if (grandparent is null)
            return false;

        string temp;
        try { temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd('\\'); }
        catch { return false; }

        return string.Equals(grandparent.TrimEnd('\\'), temp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when an ELEVATED <see cref="CommandPolicyProfile.OfficialUninstaller"/> command whose executable is
    /// <paramref name="expandedPath"/> may run: either it is contained under <paramref name="allowedRoot"/> (the
    /// app's own install directory — segment-boundary containment via the SAME
    /// <see cref="LeftoverClassifier.IsPathUnder"/> used elsewhere, Fix 7, on the EXPANDED path so <c>PROGRA~1</c>
    /// does not over-block), OR it is the narrow NSIS carve-out. A missing / UNC / un-rooted
    /// <paramref name="allowedRoot"/> can never anchor → only the NSIS carve-out can admit it. msiexec is handled
    /// separately by the gate's System32 pin and never reaches here.
    /// </summary>
    internal static bool IsElevatedUninstallerAnchored(string expandedPath, string? allowedRoot)
    {
        if (IsNsisUninstaller(expandedPath))
            return true;

        if (!IsUsableAnchorRoot(allowedRoot))
            return false;

        return LeftoverClassifier.IsPathUnder(expandedPath, allowedRoot!);
    }

    /// <summary>
    /// PLANNER pre-flight (best-effort, EXPANSION-FREE): true when an elevated official uninstaller could POSSIBLY
    /// anchor — i.e. it is the NSIS carve-out, OR a usable (local, drive-rooted) anchor root exists. The planner has
    /// no <see cref="IPathCanonicalizer"/>, so it MUST NOT run the path-containment check (<see cref="IsPathUnder"/>)
    /// on the raw exe: a registry-stored 8.3 path like <c>C:\PROGRA~1\App\unins000.exe</c> with InstallLocation
    /// <c>C:\Program Files\App</c> would FALSELY fail containment and over-block, even though the GATE allows it after
    /// 8.3 expansion (cx REJECT). So the planner returns null (→ manual fallback) ONLY when anchoring is impossible
    /// regardless of expansion (no usable root AND not NSIS); the GATE stays the authoritative containment check on
    /// the EXPANDED path. The planner is therefore never STRICTER than the gate (no divergence / no over-block).
    /// </summary>
    internal static bool CanPossiblyAnchorElevatedUninstaller(string rawPath, string? allowedRoot)
        => IsNsisUninstaller(rawPath) || IsUsableAnchorRoot(allowedRoot);

    /// <summary>
    /// An anchor root is usable only when it is a non-empty, LOCAL drive-rooted path (e.g. <c>C:\...</c>). A
    /// missing, UNC (<c>\\server\...</c>), or un-rooted InstallLocation can never anchor an elevated uninstaller.
    /// </summary>
    internal static bool IsUsableAnchorRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;
        string r = root.Trim();
        if (r.StartsWith(@"\\", StringComparison.Ordinal))
            return false; // UNC
        return r.Length >= 2 && char.IsLetter(r[0]) && r[1] == ':' && System.IO.Path.IsPathFullyQualified(r);
    }
}

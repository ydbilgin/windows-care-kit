using System.Text;
using System.Text.RegularExpressions;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Migration.Detection;

/// <summary>
/// Pure, deterministic join-key helpers. No IO, no runtime AI. Used during projection and dedup.
/// </summary>
public static partial class ProgramJoinKeys
{
    // Strict anchored GUID pattern: {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}
    // All hex; curly braces required; exact segment lengths.
    [GeneratedRegex(
        @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$",
        RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex GuidPattern();

    // Trailing version suffix: one or more dotted-numeric segments at the end of the name.
    [GeneratedRegex(@"\s+\d+(\.\d+)*$", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex TrailingVersion();

    // Architecture / edition tokens, matched only as WHOLE tokens (review nit: a boundary-less Replace
    // over-strips substrings — "Linux64"→"linu", "x64dbg"→"dbg", "max86"→"max"). The lookarounds require a
    // non-alphanumeric boundary on each side, so the token must stand alone (input is already casefolded).
    [GeneratedRegex(
        @"(?<![a-z0-9])(?:\(64-bit\)|\(32-bit\)|64-bit|32-bit|x64|x86)(?![a-z0-9])",
        RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex ArchTokenPattern();

    /// <summary>
    /// Returns the lowercase MSI ProductCode GUID if <paramref name="registryKeyName"/> is exactly a
    /// well-formed GUID (with braces, all-hex segments of the correct lengths); otherwise null.
    /// </summary>
    public static string? TryProductCode(string registryKeyName)
    {
        if (string.IsNullOrEmpty(registryKeyName))
            return null;
        return GuidPattern().IsMatch(registryKeyName)
            ? registryKeyName.ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// Tier-4 join key (B.6 / B.7): NFKC-normalize → invariant casefold → collapse interior whitespace →
    /// strip architecture/edition tokens → strip trailing version suffix → trim.
    /// Non-ASCII characters (e.g. umlauts, CJK) are preserved — no ASCII folding.
    /// </summary>
    public static string NormalizeName(string displayName)
    {
        if (string.IsNullOrEmpty(displayName))
            return string.Empty;

        // NFKC normalization (compatibility decompose + canonical compose).
        string nfkc = displayName.Normalize(NormalizationForm.FormKC);

        // Invariant casefold (never CurrentCulture — deterministic across locales).
        string cased = nfkc.ToLowerInvariant();

        // Collapse interior whitespace to single space.
        string collapsed = CollapseWhitespace(cased);

        // Strip architecture/edition tokens as WHOLE tokens only (see ArchTokenPattern).
        string stripped = ArchTokenPattern().Replace(collapsed, " ");

        // Collapse whitespace again after token removal.
        stripped = CollapseWhitespace(stripped);

        // Strip trailing version suffix (e.g. " 2.0", " 10.1.2").
        stripped = TrailingVersion().Replace(stripped, string.Empty);

        return stripped.Trim();
    }

    /// <summary>
    /// Returns the lowercase leaf segment (last path component) of the canonical <paramref name="installLocation"/>,
    /// after resolving via <paramref name="canon"/>. Returns null when the location is blank or the
    /// leaf is empty (B.8: fail-open-to-literal; canon failure is non-fatal).
    /// </summary>
    public static string? InstallPathLeaf(string? installLocation, IPathCanonicalizer canon)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
            return null;

        string resolved;
        try
        {
            resolved = canon.Canonicalize(installLocation).FinalPath;
        }
        catch
        {
            // Fail-open to literal: if canon throws (e.g. invalid path), use the raw value.
            resolved = installLocation;
        }

        // Trim trailing separators before extracting the last segment.
        string trimmed = resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string leaf = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(leaf) ? null : leaf.ToLowerInvariant();
    }

    // ── Private helpers ──────────────────────────────────────────────────────────────────────────

    private static string CollapseWhitespace(string s)
    {
        if (!s.Any(char.IsWhiteSpace))
            return s;
        var sb = new StringBuilder(s.Length);
        bool lastWasSpace = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && sb.Length > 0)
                    sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }
        return sb.ToString();
    }
}

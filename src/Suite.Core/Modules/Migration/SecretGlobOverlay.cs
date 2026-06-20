using System.Text.RegularExpressions;

namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// The global secret-exclusion overlay applied ON TOP OF every recipe (critic fix F3). The copy engine
/// already refuses ~24 fixed browser credential/cookie LEAVES (<c>CopyAdapter.ForbiddenSourceLeaves</c>);
/// this overlay adds GLOB-based credential exclusions (key material, tokens) so a careless or hostile recipe
/// cannot pull a private key or token file just by listing it in <c>include</c>.
///
/// <para><b>Priority is forbidden-first (F3):</b> a recipe's <c>include</c> allow-list can NEVER override a
/// secret-glob match — these globs are merged into the copy action's <c>ExcludeLeaves</c>, and the engine
/// evaluates exclusion before include (see <c>CopyAdapter.Exclusions.AllowsFile</c>: leaf/forbidden checks
/// run before the include filter).</para>
///
/// <para><b>No over-claim (F3):</b> this is NAME-based exclusion. It does not, and does not claim to, catch a
/// DPAPI blob under an arbitrary name — content-based "never readable" magic is explicitly NOT asserted.
/// DPAPI/machine-locked data is handled by classification (<see cref="PortabilityBadge"/>), not a blind
/// guarantee.</para>
/// </summary>
public static class SecretGlobOverlay
{
    /// <summary>The credential/token leaf-name globs excluded on top of every recipe (F3).</summary>
    public static readonly IReadOnlyList<string> Globs = new[]
    {
        "*.key", "*.pem", "id_rsa*", "*.ppk", "*token*", "*secret*", "*credential*",
    };

    private static readonly Regex[] Compiled = Globs.Select(Compile).ToArray();

    /// <summary>True when a file LEAF name matches one of the secret globs (case-insensitive).</summary>
    public static bool IsSecretLeaf(string leaf)
    {
        if (string.IsNullOrEmpty(leaf))
            return false;
        foreach (Regex rx in Compiled)
            if (rx.IsMatch(leaf))
                return true;
        return false;
    }

    private static Regex Compile(string glob)
    {
        // Leaf-name glob: '*' → any run of non-separator chars. Anchored full-match, case-insensitive.
        string pattern = "^" + Regex.Escape(glob).Replace("\\*", "[^\\\\/]*") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

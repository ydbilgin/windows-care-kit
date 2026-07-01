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
/// evaluates exclusion before include (see <c>CopyAdapter.Exclusions.EvaluateFile</c>: leaf/forbidden checks
/// run before the include filter).</para>
///
/// <para><b>Content pass:</b> name-based exclusion is paired with
/// <see cref="EmbeddedSecretScanner"/> at the copy boundary so innocuous config names containing embedded
/// tokens are dropped before bytes are written.</para>
///
/// <para><b>AI-CLI credential leaves (2026-07-01 leak fix):</b> a council audit found the broad
/// <c>.codex</c>/<c>.gemini</c> backup recipes (which exclude only log/cache dirs) would package
/// <c>auth.json</c> (Codex OAuth token) and <c>oauth_creds.json</c> (Gemini OAuth token) — neither matched the
/// original globs. Those names, plus the owner's own documented hard-rule credential filenames
/// (<c>.env*</c>, <c>.npmrc</c>, <c>cred_blob*</c>) and the non-RSA SSH private-key types, are added here so
/// they are pruned forbidden-first at copy time for EVERY recipe. Still name-based, per the honesty note above.</para>
/// </summary>
public static class SecretGlobOverlay
{
    /// <summary>
    /// The credential/token leaf-name globs excluded on top of every recipe (F3 + 2026-07-01 AI-CLI leak fix).
    /// The concrete file-family patterns here mirror the SECURITY DENY-LIST in .gitignore. The broad
    /// content-name patterns (*token*/*secret*/*credential*) are deliberately NOT in .gitignore — there they
    /// would wrongly ignore this repo's own security-named source files (SecretGlobOverlay, EmbeddedSecretScanner,
    /// SecretFilter tests). This overlay guards BACKUP sources; .gitignore guards repo commits.
    /// </summary>
    public static readonly IReadOnlyList<string> Globs = new[]
    {
        // Key material / tokens (original F3 set) + all SSH private-key types (id_rsa* only caught RSA).
        "*.key", "*.pem", "*.pfx", "*.p12", "*.pgp", "*.gpg", "*.asc", "*.jks", "*.keystore",
        "*.kdbx", "*.ovpn", "*.mobileprovision", "id_rsa*", "id_ed25519*", "id_ecdsa*", "id_dsa*", "*.ppk",
        "*token*", "*secret*", "*credential*", "*password*", "*recovery-code*",
        // AI-CLI credential/token leaves + owner hard-rule credential filenames (2026-07-01 leak fix).
        // `.env` + `.env.*` (not `.env*`) targets the dotenv file family without over-matching a directory
        // such as `.environment` (adversarial-review LOW finding).
        "auth.json", "oauth_creds.json", ".npmrc", ".env", ".env.*", "cred_blob*",
        "wallet.dat", ".git-credentials", ".netrc", ".pypirc", ".claude.json",
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

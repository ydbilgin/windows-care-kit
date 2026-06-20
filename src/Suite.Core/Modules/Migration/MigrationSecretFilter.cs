namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// The single decision point for "may this leaf be copied?" with the correct priority order (critic fix F3):
/// <list type="number">
/// <item>the engine's built-in fixed credential leaves (Chromium/Firefox cookie/password stores);</item>
/// <item>the global secret-glob overlay (<see cref="SecretGlobOverlay"/>: <c>*.key</c>, <c>id_rsa*</c>, …);</item>
/// <item>ONLY THEN the recipe's <c>include</c> allow-list.</item>
/// </list>
/// Forbidden-first is the whole point: a recipe that lists <c>id_rsa</c> (or globs <c>**</c>) in its include
/// can NOT override the secret exclusion — the secret check returns false before include is even consulted.
///
/// <para>Honesty (F3): this is NAME-based. It does not claim to recognize a DPAPI blob hidden under an
/// arbitrary name — that data is handled by <see cref="PortabilityBadge"/> classification, not a blind
/// "never readable" guarantee.</para>
/// </summary>
public static class MigrationSecretFilter
{
    /// <summary>
    /// True when a file with leaf name <paramref name="leaf"/> is allowed to be copied under the overlay.
    /// <paramref name="forbiddenFixedLeaves"/> is the engine's built-in fixed superset (case-insensitive).
    /// Forbidden/secret matches always win, regardless of include.
    /// </summary>
    public static bool IsLeafAllowed(string leaf, IReadOnlyCollection<string> forbiddenFixedLeaves)
    {
        if (string.IsNullOrEmpty(leaf))
            return false;

        // (1) built-in fixed credential leaves — forbidden first.
        foreach (string f in forbiddenFixedLeaves)
            if (string.Equals(f, leaf, StringComparison.OrdinalIgnoreCase))
                return false;

        // (2) global secret-glob overlay — also forbidden, before any include can speak.
        if (SecretGlobOverlay.IsSecretLeaf(leaf))
            return false;

        // (3) not a secret → allowed (the recipe's own include allow-list is applied by the copy engine).
        return true;
    }
}

using System.Text.RegularExpressions;

namespace WindowsCareKit.Core.Modules.Migration.Discovery;

/// <summary>
/// The central cache/junk-dir exclusion overlay used during discovery walks. Mirrors <see cref="SecretGlobOverlay"/>
/// in shape (compiled regex, static <c>IsCacheLeaf</c>, exposed <c>Globs</c> list).
///
/// <para><b>Forbidden-first during discovery:</b> a dir whose leaf matches <see cref="IsCacheLeaf"/> is PRUNED
/// before descent — its entries are never counted against the budget and never contribute to activity.</para>
///
/// <para><b>Defeasible at backup time (F4):</b> unlike <see cref="SecretGlobOverlay"/> (absolute),
/// cache exclusion MUST yield to an explicit user Include at backup time. PR-2 MUST NOT merge these globs
/// into <c>ExcludeLeaves</c> via <c>CopyAdapter.Exclusions</c> (which gives exclude absolute priority).
/// Instead PR-2 MUST implement a mechanism that lets Include override a cache-glob match — see
/// <c>CopyAdapter.cs:350-363</c>.</para>
///
/// <para><b>TODO — PR-2 handoff contract (binding):</b> the materialization step MUST apply these globs
/// (as Include-defeasible excludes) at backup time so a discovery→recipe draft cannot re-walk the
/// pruned cache dirs. Either (a) inject <see cref="Globs"/> into the draft recipe's <c>exclude</c>
/// list, or (b) add <see cref="CacheGlobOverlay"/> as a second (defeasible) overlay in
/// <c>RecipeToBackupEntry.Bridge</c> — with a handoff-exclude-contract test. See
/// DISCOVERY_DECISION F1/F4.</para>
/// </summary>
public static class CacheGlobOverlay
{
    /// <summary>
    /// Cache/junk dir leaf-name globs (narrowed per F4 to known junk, avoiding overly-generic patterns
    /// that could prune real user data). Note that <c>*Cache*</c> is kept because project seed recipes
    /// already use it and in DISCOVERY a false-positive prune only under-lists, never deletes.
    /// </summary>
    public static readonly IReadOnlyList<string> Globs = new[]
    {
        "node_modules",
        "blob_storage",
        "GPUCache",
        "Code Cache",
        "Cache",
        "Cache_Data",
        "Crashpad",
        "ShaderCache",
        "Service Worker",
        "CacheStorage",
        "*Cache*",
    };

    private static readonly Regex[] Compiled = Globs.Select(Compile).ToArray();

    /// <summary>
    /// True when a directory or file LEAF name matches one of the cache globs (case-insensitive).
    /// Call before descending into a directory or before counting a file leaf toward activity.
    /// </summary>
    public static bool IsCacheLeaf(string leaf)
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

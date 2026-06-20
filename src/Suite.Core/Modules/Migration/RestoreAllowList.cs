namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// The Slice 2 restore ALLOW-LIST (decision §F2). The first restore sub-slice writes back ONLY a small set of
/// single-file config restores whose CONTENT is inner-path clean — i.e. the file body does not embed absolute
/// paths or user SIDs that would need rewriting on the new machine. For these, placing the file at the correct
/// profile-relative <see cref="KnownFolder"/> location is sufficient; no in-file rebind is needed.
///
/// <para><b>Explicitly deferred to Slice 3:</b> in-file absolute-path / SID rebind (M17). Slice 2 NEVER edits
/// the BYTES of a restored config — it only relocates the file. A recipe whose config embeds machine-specific
/// inner paths is NOT on this allow-list and is skipped by the runner, so no blind content-replace can happen.</para>
/// </summary>
public static class RestoreAllowList
{
    /// <summary>
    /// Recipe ids whose configs are inner-path clean enough to restore by file-placement alone in Slice 2.
    /// Kept deliberately tiny (git <c>.gitconfig</c>, Claude <c>settings.json</c> are the canonical examples).
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedRecipeIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "git.config",
            "anthropic.claude-code",
        };

    /// <summary>True when this recipe's configs may be restored by the Slice 2 file-placement runner.</summary>
    public static bool IsAllowed(string? recipeId)
        => !string.IsNullOrWhiteSpace(recipeId) && AllowedRecipeIds.Contains(recipeId.Trim());
}

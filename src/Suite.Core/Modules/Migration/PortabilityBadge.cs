namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>The portability badge shown to the user (decision §"Rozet"). The glyph is fail-safe by design.</summary>
public enum BadgeKind
{
    /// <summary>✅ Profile-relative, no preconditions — rebinds cleanly on the new machine.</summary>
    PortableClean,

    /// <summary>🔁 Profile-relative but has preconditions (e.g. process-closed) — portable with a step.</summary>
    PortableWithStep,

    /// <summary>⚠️ Partial — some data is portable, some is not.</summary>
    Partial,

    /// <summary>❌ Machine-locked — inventory/warn only; NEVER shown as a confident "it works" badge.</summary>
    MachineLocked,
}

/// <summary>The computed badge for one migration item: glyph + whether it may be presented as "works".</summary>
/// <param name="Kind">The badge kind.</param>
/// <param name="Glyph">The display glyph (✅/🔁/⚠️/❌).</param>
/// <param name="MayClaimWorks">
/// True ONLY when it is safe to present this as "will work on the new machine". The fail-safe (decision
/// §"sahte-güven önleme"): this is false for anything machine-locked or partial — a green "works" badge is
/// never drawn for data that could silently fail after restore.
/// </param>
public sealed record PortabilityBadgeResult(BadgeKind Kind, string Glyph, bool MayClaimWorks);

/// <summary>
/// PURE-function badge computation (decision §D: no IO). Given a portability class and whether the item has
/// restore preconditions, it returns the badge. The single hard rule (the project's biggest stated risk):
/// a <see cref="PortabilityClass.MachineLocked"/> (or partial) item NEVER yields <c>MayClaimWorks == true</c>.
/// The UI later only DISPLAYS this; the fail-safe lives here, before any restore-execution exists.
/// </summary>
public static class PortabilityBadge
{
    /// <summary>Back-compat overload: no declared-secret signal (equivalent to <c>hasExcludedSecret: false</c>).</summary>
    public static PortabilityBadgeResult Compute(PortabilityClass cls, bool hasPreconditions)
        => Compute(cls, hasPreconditions, hasExcludedSecret: false, hasMachineBoundContent: false, hasUnanalyzedContent: false);

    /// <summary>
    /// PURE badge computation with the B-1 secret fail-safe as a FIRST-CLASS input (decision §3A / critic#1-#2).
    /// When <paramref name="hasExcludedSecret"/> is true (the item declares a secret leaf the name-based filter
    /// excludes), an otherwise-portable item is downgraded to <see cref="BadgeKind.Partial"/> with
    /// <c>MayClaimWorks == false</c> — the green "works" claim can never be drawn over an item whose declared
    /// content includes a pruned secret. The override is here, in the pure function, so no UI layer can fork it.
    /// </summary>
    public static PortabilityBadgeResult Compute(PortabilityClass cls, bool hasPreconditions, bool hasExcludedSecret)
        => Compute(cls, hasPreconditions, hasExcludedSecret, hasMachineBoundContent: false, hasUnanalyzedContent: false);

    /// <summary>
    /// PURE badge computation with both B-1 floors. Content evidence is stronger than an optimistic declaration
    /// and yields a machine-locked badge. Catalog tier and UI code cannot bypass this function.
    /// </summary>
    public static PortabilityBadgeResult Compute(
        PortabilityClass cls,
        bool hasPreconditions,
        bool hasExcludedSecret,
        bool hasMachineBoundContent)
        => Compute(cls, hasPreconditions, hasExcludedSecret, hasMachineBoundContent, hasUnanalyzedContent: false);

    /// <summary>
    /// PURE badge computation with distinct "not analyzed" vocabulary. Unanalyzed local bytes may not claim
    /// works, but they are not rendered as machine-locked evidence.
    /// </summary>
    public static PortabilityBadgeResult Compute(
        PortabilityClass cls,
        bool hasPreconditions,
        bool hasExcludedSecret,
        bool hasMachineBoundContent,
        bool hasUnanalyzedContent) => cls switch
    {
        PortabilityClass.ProfileRelative when hasMachineBoundContent
            => new PortabilityBadgeResult(BadgeKind.MachineLocked, "❌", MayClaimWorks: false),
        PortabilityClass.ProfileRelative when hasUnanalyzedContent
            => new PortabilityBadgeResult(BadgeKind.Partial, "⚠️", MayClaimWorks: false),
        // B-1: a declared secret on an otherwise-portable item can never be presented as a confident "works".
        PortabilityClass.ProfileRelative when hasExcludedSecret
            => new PortabilityBadgeResult(BadgeKind.Partial, "⚠️", MayClaimWorks: false),
        PortabilityClass.ProfileRelative when hasPreconditions
            => new PortabilityBadgeResult(BadgeKind.PortableWithStep, "🔁", MayClaimWorks: true),
        PortabilityClass.ProfileRelative
            => new PortabilityBadgeResult(BadgeKind.PortableClean, "✅", MayClaimWorks: true),
        PortabilityClass.Partial
            => new PortabilityBadgeResult(BadgeKind.Partial, "⚠️", MayClaimWorks: false),
        PortabilityClass.MachineLocked
            => new PortabilityBadgeResult(BadgeKind.MachineLocked, "❌", MayClaimWorks: false),
        // Fail-closed for any unmodeled class: never claim it works.
        _ => new PortabilityBadgeResult(BadgeKind.MachineLocked, "❌", MayClaimWorks: false),
    };

    /// <summary>Convenience overload computing the badge straight from an item's restore META (reads the B-1 signal).</summary>
    public static PortabilityBadgeResult Compute(MigrationItemMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return Compute(
            meta.PortabilityClass,
            meta.Preconditions.Count > 0,
            meta.HasExcludedSecret,
            meta.HasMachineBoundContent,
            meta.HasUnanalyzedContent);
    }
}

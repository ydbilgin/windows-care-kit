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
    public static PortabilityBadgeResult Compute(PortabilityClass cls, bool hasPreconditions) => cls switch
    {
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

    /// <summary>Convenience overload computing the badge straight from an item's restore META.</summary>
    public static PortabilityBadgeResult Compute(MigrationItemMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return Compute(meta.PortabilityClass, meta.Preconditions.Count > 0);
    }
}

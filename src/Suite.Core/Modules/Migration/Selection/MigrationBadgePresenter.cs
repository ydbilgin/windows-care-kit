namespace WindowsCareKit.Core.Modules.Migration.Selection;

/// <summary>
/// UI vocabulary derived from the core badge plus the orthogonal restore-tier capability gate.
/// <see cref="CoreBadge"/> remains the single portability truth.
/// </summary>
public sealed record MigrationBadgePresentation(
    PortabilityBadgeResult CoreBadge,
    BadgeKind DisplayKind,
    string Glyph,
    bool MayClaimWorks,
    bool HasSecretOverlay,
    bool HasRegenerableOverlay,
    bool IsRestoreTierCapped,
    string LabelTr,
    string LabelEn);

public static class MigrationBadgePresenter
{
    /// <summary>
    /// Derive the visible badge. Even when portability is clean, InventoryOnly may not render green/"works".
    /// Secret state comes only from the B-1 signal already consumed by <see cref="PortabilityBadge.Compute(MigrationItemMeta)"/>.
    /// </summary>
    public static MigrationBadgePresentation Derive(
        MigrationItemMeta meta,
        RestoreTier restoreTier,
        bool isRegenerable)
    {
        ArgumentNullException.ThrowIfNull(meta);
        PortabilityBadgeResult core = PortabilityBadge.Compute(meta);
        bool tierAllowsRestoreClaim = restoreTier >= RestoreTier.ConfigCopy;
        bool mayClaimWorks = core.MayClaimWorks && tierAllowsRestoreClaim;
        bool tierCapped = core.MayClaimWorks && !tierAllowsRestoreClaim;

        BadgeKind displayKind = tierCapped ? BadgeKind.Partial : core.Kind;
        string glyph = displayKind switch
        {
            BadgeKind.PortableClean => "✅",
            BadgeKind.PortableWithStep => "🔁",
            BadgeKind.Partial => "⚠️",
            _ => "❌",
        };
        (string tr, string en) = displayKind switch
        {
            BadgeKind.PortableClean => ("taşınabilir", "portable"),
            BadgeKind.PortableWithStep => ("adım gerekli", "step required"),
            BadgeKind.Partial when tierCapped => ("yalnız envanter / manuel", "inventory / manual only"),
            BadgeKind.Partial => ("kısmi", "partial"),
            _ => ("makine-kilitli", "machine-locked"),
        };

        return new MigrationBadgePresentation(
            core,
            displayKind,
            glyph,
            mayClaimWorks,
            HasSecretOverlay: meta.HasExcludedSecret,
            HasRegenerableOverlay: isRegenerable,
            IsRestoreTierCapped: tierCapped,
            LabelTr: tr,
            LabelEn: en);
    }
}

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
            BadgeKind.Partial when meta.HasUnanalyzedContent => ("incelenemedi", "not analyzed"),
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

/// <summary>The count of parts sharing the app's best (least severe) badge kind, out of the total part count.
/// Only produced when parts genuinely differ (A5) — a uniform app has nothing to break down.</summary>
public sealed record BadgeBreakdown(int BestCount, int Total, string LabelTr, string LabelEn);

/// <summary>
/// A5 worst-of aggregation: an app's badge is derived from its parts as a PURE function, never decided in the
/// UI. Severity order (worst wins): machine-locked &gt; partial/not-analyzed &gt; step-required &gt; portable.
/// A badge may only DOWNGRADE under aggregation — it is always exactly one part's own (unmodified) presentation,
/// never an average or a new synthesized state.
/// </summary>
public static class MigrationBadgeAggregator
{
    /// <summary>
    /// The app-level badge: the worst part's presentation, with <see cref="MigrationBadgePresentation.MayClaimWorks"/>
    /// re-derived as <c>AND(parts)</c> (an app may claim "works" only if every part may) and the secret-excluded
    /// overlay re-derived as <c>OR(parts)</c> (shown if any part has it) — both applied on top of the worst part's
    /// own kind/glyph/labels, never averaged.
    /// </summary>
    public static MigrationBadgePresentation Aggregate(IReadOnlyList<MigrationBadgePresentation> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
            throw new ArgumentException("an app must own at least one part badge", nameof(parts));

        // Equal-severity ties use an explicit honesty precedence: a restore-tier cap outranks a generic partial
        // explanation, then labels are sorted ordinally. Reordering parts can therefore never hide the cap.
        MigrationBadgePresentation worst = parts
            .OrderByDescending(p => Severity(p.DisplayKind))
            .ThenByDescending(p => p.IsRestoreTierCapped)
            .ThenBy(p => p.LabelEn, StringComparer.Ordinal)
            .First();
        return worst with
        {
            MayClaimWorks = parts.All(p => p.MayClaimWorks),
            HasSecretOverlay = parts.Any(p => p.HasSecretOverlay),
            HasRegenerableOverlay = parts.Any(p => p.HasRegenerableOverlay),
            IsRestoreTierCapped = parts.Any(p => p.IsRestoreTierCapped),
        };
    }

    /// <summary>
    /// A5's "never hide good news dishonestly" clause: when parts differ, the count of parts sharing the BEST
    /// (least severe) kind, out of the total — e.g. 3 of 4 parts are portable while the worst-of pill leads with
    /// the 4th part's machine-locked state. Returns null when every part shares the same kind (nothing to break
    /// down — the worst-of pill already tells the whole truth).
    /// </summary>
    public static BadgeBreakdown? Breakdown(IReadOnlyList<MigrationBadgePresentation> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count <= 1)
            return null;

        int bestSeverity = parts.Min(p => Severity(p.DisplayKind));
        MigrationBadgePresentation[] best = parts.Where(p => Severity(p.DisplayKind) == bestSeverity).ToArray();
        if (best.Length == parts.Count)
            return null;

        return new BadgeBreakdown(best.Length, parts.Count, best[0].LabelTr, best[0].LabelEn);
    }

    // Higher = worse. machine-locked > partial/not-analyzed > step-required > portable (A5).
    private static int Severity(BadgeKind kind) => kind switch
    {
        BadgeKind.MachineLocked => 3,
        BadgeKind.Partial => 2,
        BadgeKind.PortableWithStep => 1,
        BadgeKind.PortableClean => 0,
        _ => 3,
    };
}

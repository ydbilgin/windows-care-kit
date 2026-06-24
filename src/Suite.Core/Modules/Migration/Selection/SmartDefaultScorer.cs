namespace WindowsCareKit.Core.Modules.Migration.Selection;

public enum SmartDefaultKind
{
    Off,
    On,
    ForcedOnCritical,
}

/// <summary>The deterministic three-factor score and resulting default selection.</summary>
public sealed record SmartDefaultDecision(int IrreplaceabilityScore, SmartDefaultKind Kind, string ReasonCode);

public static class SmartDefaultScorer
{
    /// <summary>
    /// Score = no-cloud-backup + on-system-drive + unique/non-regenerable. Only score 3 is pre-checked.
    /// Risky, inventory-only, auto-stub, and unrecognized rows remain opt-in. OneDrive redirected with sync
    /// off is the explicit silent-data-loss exception and is forced on with a red UI reason.
    /// </summary>
    public static SmartDefaultDecision Score(
        MigrationSelectionCandidate candidate,
        MigrationBadgePresentation badge)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(badge);

        int score =
            (candidate.HasCloudBackup ? 0 : 1)
            + (candidate.IsOnSystemDrive ? 1 : 0)
            // §D.3 "unique/non-regenerable" is intentionally OR: either property earns factor 3.
            + (candidate.IsUnique || !candidate.IsRegenerable ? 1 : 0);

        if (candidate.OneDriveRedirectedSyncOff)
            return new SmartDefaultDecision(score, SmartDefaultKind.ForcedOnCritical, "onedrive-redirected-sync-off");

        if (!candidate.IsRecognized || candidate.IsAutoStub)
            return new SmartDefaultDecision(score, SmartDefaultKind.Off, "manual-review-only");

        if (!badge.MayClaimWorks
            || badge.DisplayKind is BadgeKind.Partial or BadgeKind.MachineLocked)
            return new SmartDefaultDecision(score, SmartDefaultKind.Off, "risk-bucket-opt-in");

        return score == 3
            ? new SmartDefaultDecision(score, SmartDefaultKind.On, "top-irreplaceability-tier")
            : new SmartDefaultDecision(score, SmartDefaultKind.Off, "below-top-tier");
    }
}

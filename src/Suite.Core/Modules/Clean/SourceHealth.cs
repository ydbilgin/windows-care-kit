namespace WindowsCareKit.Core.Modules.Clean;

/// <summary>
/// How completely a read-only inventory source could be inspected (NEW-07 honesty: "could not inspect"
/// must never render identically to "inspected and found nothing").
/// </summary>
public enum SourceHealth
{
    /// <summary>The source was fully inspected; the returned items are the complete set.</summary>
    Complete,

    /// <summary>The source was only partly inspected: at least one sub-source (a hive, folder, or profile)
    /// could not be read, so the returned items may be missing entries.</summary>
    Partial,

    /// <summary>The source could not be inspected at all; the returned items carry no information about it.
    /// A consumer MUST NOT render this as an empty/complete result.</summary>
    Unavailable,
}

/// <summary>
/// A named sub-source that could not be read, with a SAFE failure category — an exception type name or a
/// numeric code, NEVER a path, message, or payload (NEW-07 / P26: preserve the failure category without
/// leaking sensitive detail). <paramref name="Source"/> is a fixed, non-sensitive descriptor owned by the
/// adapter (e.g. "HKLM Run", "Chrome/Default"), never user data.
/// </summary>
public sealed record InventorySourceFault(string Source, string Category);

/// <summary>
/// Single owner of the Complete/Partial/Unavailable rule for a multi-sub-source inventory (startup keys +
/// folders, browser profiles), so no adapter re-derives it inconsistently (P22).
/// </summary>
public static class InventoryHealth
{
    /// <summary>
    /// Aggregate an inventory's health from how many sub-sources were <paramref name="attempted"/> (reached the
    /// point of reading — an absent hive/folder/profile is neither attempted nor failed) and how many of those
    /// <paramref name="failed"/> (threw). No failures → <see cref="SourceHealth.Complete"/>; every attempted
    /// sub-source failed → <see cref="SourceHealth.Unavailable"/>; some but not all failed →
    /// <see cref="SourceHealth.Partial"/>. Attempting zero sub-sources is <see cref="SourceHealth.Complete"/>
    /// (there was legitimately nothing to inspect — an honest empty, not a failure).
    /// </summary>
    public static SourceHealth Aggregate(int attempted, int failed)
    {
        if (attempted < 0)
            throw new ArgumentOutOfRangeException(nameof(attempted));
        if (failed < 0 || failed > attempted)
            throw new ArgumentOutOfRangeException(nameof(failed));

        if (failed == 0)
            return SourceHealth.Complete;
        if (failed == attempted)
            return SourceHealth.Unavailable;
        return SourceHealth.Partial;
    }
}

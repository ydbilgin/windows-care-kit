namespace WindowsCareKit.Core.Modules.Migration.Discovery;

/// <summary>
/// The outcome of scanning one candidate app directory. The status tells the caller what quality of
/// information is available — an incomplete or inaccessible scan is surfaced honestly rather than
/// omitted or silently treated as inactive.
/// </summary>
public enum DiscoveryScanStatus
{
    /// <summary>All entries were visited within budget.</summary>
    Complete,

    /// <summary>
    /// The global entry-count budget (<see cref="DiscoveryScanOptions.MaxGlobalEntries"/>) was exhausted
    /// before this app (or during it). The app is still emitted; <see cref="DiscoveredApp.LastModifiedUtc"/>
    /// reflects the entries actually seen, not a fabricated time.
    /// </summary>
    IncompleteBudget,

    /// <summary>The <see cref="System.Threading.CancellationToken"/> was cancelled during the scan.</summary>
    Cancelled,

    /// <summary>Access errors prevented any entries from being read. The app is still emitted.</summary>
    Inaccessible,

    /// <summary>
    /// The candidate app directory IS itself a reparse point (junction/symlink). It is surfaced so the
    /// user can see junction-relocated apps; it is never traversed (spec §F3 — do not silently omit).
    /// <see cref="DiscoveredApp.LastModifiedUtc"/> is null for this status.
    /// </summary>
    NotTraversedReparse,
}

/// <summary>
/// Safe aggregate metadata for one discovered app candidate. Contains ONLY aggregate information —
/// no leaf file paths, no matched secret or cache glob names — so the inventory itself cannot leak
/// sensitive filenames (spec §"DiscoveredApp carries only SAFE AGGREGATE metadata").
/// </summary>
/// <param name="Id">Stable identifier — the candidate directory's leaf name.</param>
/// <param name="DisplayName">Human-readable name (same as <paramref name="Id"/> in PR-1; may be enriched in a later PR).</param>
/// <param name="Root">Which profile root (<see cref="KnownFolder"/>) this app lives under.</param>
/// <param name="RelativePath">Path relative to the profile root directory.</param>
/// <param name="LastModifiedUtc">
/// Max <c>LastWriteTimeUtc</c> of ALLOWED (non-secret, non-cache) file leaves seen, or null when
/// none were seen (incomplete scan, reparse, or no allowed files). Never fabricated.
/// </param>
/// <param name="Portability">Portability class. Always <see cref="PortabilityClass.Partial"/> in PR-1.</param>
/// <param name="Status">The quality/completeness of the scan that produced this record.</param>
public sealed record DiscoveredApp(
    string Id,
    string DisplayName,
    KnownFolder Root,
    string RelativePath,
    DateTime? LastModifiedUtc,
    PortabilityClass Portability,
    DiscoveryScanStatus Status);

namespace WindowsCareKit.Core.Modules.Migration.Detection;

/// <summary>
/// A normalized, deterministic program record produced by the detection spine. Contains only safe,
/// observable fields — no paths that could leak sensitive data, no runtime AI. Init-only and sealed.
/// </summary>
public sealed record DiscoveredProgram
{
    /// <summary>
    /// Stable identifier. Precedence: MSI ProductCode → PackageFamilyName → InstallPathLeaf → "NormalizedName|publisher".
    /// </summary>
    public required string Id { get; init; }

    /// <summary>The DisplayName as read from the source.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Publisher / vendor, or null when absent.</summary>
    public string? Publisher { get; init; }

    /// <summary>DisplayVersion string, or null when absent.</summary>
    public string? Version { get; init; }

    /// <summary>Raw InstallLocation value from the registry, or null.</summary>
    public string? InstallLocation { get; init; }

    /// <summary>
    /// Canonical, lowercase leaf segment of InstallLocation (B-3 join key). Null when InstallLocation
    /// is absent or yields an empty segment.
    /// </summary>
    public string? InstallPathLeaf { get; init; }

    /// <summary>MSI ProductCode in lowercase GUID form, or null when the key name is not a GUID.</summary>
    public string? ProductCode { get; init; }

    /// <summary>Tier-4 join key: NFKC + casefold + version-token stripping (B.6 / B.7).</summary>
    public required string NormalizedName { get; init; }

    /// <summary>Installation scope.</summary>
    public required ProgramScope Scope { get; init; }

    /// <summary>
    /// Which sources contributed this record (after dedup). Distinct, ordered by <see cref="ProgramSourceKind"/>
    /// enum ordinal.
    /// </summary>
    public required IReadOnlyList<ProgramSourceKind> Sources { get; init; }

    /// <summary>
    /// True when the entry carries SystemComponent=1. These are NEVER dropped — they remain in the list
    /// so the UI can choose to show or hide them; the flag is preserved faithfully.
    /// </summary>
    public bool IsSystemComponent { get; init; }

    /// <summary>Winget package id (M1a = null; populated in a later increment).</summary>
    public string? ReinstallId { get; init; }

    /// <summary>AppX package family name (M1a = null; populated in a later increment).</summary>
    public string? PackageFamilyName { get; init; }
}

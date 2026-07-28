namespace WindowsCareKit.Core.Modules.Backup;

/// <summary>
/// Loads and merges the ported backup manifest JSON files into a single <see cref="BackupManifest"/>,
/// expanding <c>%ENV%</c> tokens in each entry's <c>source</c> via an injected
/// <see cref="IEnvironmentExpander"/> (spec §1.3 manifest-driven). Read-only: no copy happens here.
/// </summary>
public interface IManifestLoader
{
    /// <summary>Load every <c>*.json</c> manifest from <paramref name="manifestsDirectory"/> and merge their entries.</summary>
    BackupManifestLoadResult LoadFromDirectory(string manifestsDirectory);

    /// <summary>Parse and merge already-read JSON documents (one string per manifest file). Used by tests.</summary>
    BackupManifestLoadResult LoadFromJson(IEnumerable<string> jsonDocuments);
}

/// <summary>How completely the backup manifest directory was inspected.</summary>
public enum BackupManifestLoadStatus
{
    /// <summary>The optional manifest component is genuinely absent.</summary>
    NotInstalled,

    /// <summary>Every discovered manifest was read and parsed, including a legitimately empty set.</summary>
    Complete,

    /// <summary>Some manifests loaded and some failed; returned entries are intentionally partial.</summary>
    Partial,

    /// <summary>No discovered manifest could be inspected, so an empty result is not trustworthy.</summary>
    Unavailable,
}

/// <summary>The outcome of one discovered manifest file (or the directory itself if enumeration failed).</summary>
public enum BackupManifestFileStatus
{
    Loaded,
    Malformed,
    Unreadable,
}

/// <summary>
/// Per-file outcome with a safe failure category. The path identifies the failed boundary; no document
/// contents or exception message is retained.
/// </summary>
public sealed record BackupManifestFileOutcome(
    string Path,
    BackupManifestFileStatus Status,
    string? FailureCategory);

/// <summary>Honest aggregate plus the per-file evidence needed to explain partial inventory.</summary>
public sealed record BackupManifestLoadResult(
    BackupManifest Manifest,
    BackupManifestLoadStatus Status,
    IReadOnlyList<BackupManifestFileOutcome> Files)
{
    public static BackupManifestLoadResult Complete(BackupManifest manifest)
        => new(manifest, BackupManifestLoadStatus.Complete, Array.Empty<BackupManifestFileOutcome>());
}

namespace WindowsCareKit.Core.Modules.Backup;

/// <summary>
/// Loads and merges the ported backup manifest JSON files into a single <see cref="BackupManifest"/>,
/// expanding <c>%ENV%</c> tokens in each entry's <c>source</c> via an injected
/// <see cref="IEnvironmentExpander"/> (spec §1.3 manifest-driven). Read-only: no copy happens here.
/// </summary>
public interface IManifestLoader
{
    /// <summary>Load every <c>*.json</c> manifest from <paramref name="manifestsDirectory"/> and merge their entries.</summary>
    BackupManifest LoadFromDirectory(string manifestsDirectory);

    /// <summary>Parse and merge already-read JSON documents (one string per manifest file). Used by tests.</summary>
    BackupManifest LoadFromJson(IEnumerable<string> jsonDocuments);
}

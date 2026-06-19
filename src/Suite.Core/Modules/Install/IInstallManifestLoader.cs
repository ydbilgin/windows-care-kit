namespace WindowsCareKit.Core.Modules.Install;

/// <summary>
/// Loads the reinstall manifest (<c>90-kurulum.json</c>) into the typed <see cref="InstallManifest"/>.
/// Declared as its own contract (not reusing the Backup <c>IManifestLoader</c>) so the two modules stay
/// decoupled (contract §C.2). Reading the JSON is the only IO here — it is read-only.
/// </summary>
public interface IInstallManifestLoader
{
    /// <summary>Loads and parses the manifest from the given file path, assigning the restore order.</summary>
    InstallManifest Load(string manifestPath);

    /// <summary>Parses an already-read JSON document (used by tests and when the file is embedded content).</summary>
    InstallManifest Parse(string json);
}

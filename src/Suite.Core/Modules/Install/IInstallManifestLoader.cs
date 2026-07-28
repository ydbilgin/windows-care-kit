namespace WindowsCareKit.Core.Modules.Install;

/// <summary>
/// Loads the reinstall manifest (<c>90-install.json</c>) into the typed <see cref="InstallManifest"/>.
/// Declared as its own contract (not reusing the Backup <c>IManifestLoader</c>) so the two modules stay
/// decoupled (contract §C.2). Reading the JSON is the only IO here — it is read-only.
/// </summary>
public interface IInstallManifestLoader
{
    /// <summary>
    /// Loads and parses the manifest from the given file path, preserving whether it was absent, loaded,
    /// malformed, or unreadable.
    /// </summary>
    InstallManifestLoadResult Load(string manifestPath);

    /// <summary>Parses an already-read JSON document (used by tests and when the file is embedded content).</summary>
    InstallManifestLoadResult Parse(string json);
}

/// <summary>The outcome of inspecting the single install-manifest boundary.</summary>
public enum InstallManifestLoadStatus
{
    /// <summary>The optional install-manifest component is genuinely absent.</summary>
    NotInstalled,

    /// <summary>The document was read and parsed. Its manifest may legitimately contain zero entries.</summary>
    Loaded,

    /// <summary>The document was present but was blank or invalid JSON.</summary>
    Malformed,

    /// <summary>The document was present but could not be read.</summary>
    Unreadable,
}

/// <summary>
/// Honest single-file result. <paramref name="FailureCategory"/> is a safe category token, never file
/// contents or an exception message.
/// </summary>
public sealed record InstallManifestLoadResult(
    InstallManifest Manifest,
    InstallManifestLoadStatus Status,
    string ManifestPath,
    string? FailureCategory)
{
    public static InstallManifestLoadResult Loaded(InstallManifest manifest, string manifestPath)
        => new(manifest, InstallManifestLoadStatus.Loaded, manifestPath, null);
}

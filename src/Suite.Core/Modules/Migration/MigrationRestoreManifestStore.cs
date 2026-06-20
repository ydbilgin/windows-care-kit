using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>Thrown when <c>migration-manifest.json</c> is missing, malformed, or fails validation (fail-closed).</summary>
public sealed class MigrationManifestException : Exception
{
    public MigrationManifestException(string message) : base(message) { }
}

/// <summary>
/// JSON read/write for the package's <c>migration-manifest.json</c>. It uses only <c>File.ReadAllText</c> /
/// <c>File.WriteAllText</c> / <c>Directory.CreateDirectory</c> (none on the banned list — only delete/move/
/// registry/process are). On LOAD it rejects an unknown schema version and any target whose relative path is
/// not a safe profile-relative segment (F5: traversal / absolute / rooted / escape), so a hostile package can
/// never steer a restore write outside the chosen profile root before the runner even builds an action.
/// </summary>
public sealed class MigrationRestoreManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The manifest path for a package directory.</summary>
    public string PathFor(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        return Path.Combine(packageDirectory, MigrationRestoreManifest.FileName);
    }

    /// <summary>Write the manifest to the package root.</summary>
    public void Save(string packageDirectory, MigrationRestoreManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentNullException.ThrowIfNull(manifest);

        Directory.CreateDirectory(packageDirectory);
        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(PathFor(packageDirectory), json);
    }

    /// <summary>
    /// Load + VALIDATE the manifest from a package root. Throws <see cref="MigrationManifestException"/> when
    /// it is absent, unparseable, of an unknown schema version, or contains an unsafe relative/source path.
    /// </summary>
    public MigrationRestoreManifest Load(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        string path = PathFor(packageDirectory);
        if (!File.Exists(path))
            throw new MigrationManifestException($"restore manifest not found: {path}");

        MigrationRestoreManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<MigrationRestoreManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new MigrationManifestException($"restore manifest is malformed: {ex.Message}");
        }

        if (manifest is null)
            throw new MigrationManifestException("restore manifest deserialized to null");
        if (manifest.SchemaVersion != MigrationRestoreManifest.CurrentSchemaVersion)
            throw new MigrationManifestException($"unsupported manifest schema version {manifest.SchemaVersion}");

        var targets = manifest.Targets ?? Array.Empty<MigrationRestoreTarget>();
        foreach (MigrationRestoreTarget t in targets)
            Validate(t);

        return manifest with { Targets = targets };
    }

    /// <summary>
    /// F5 fail-closed validation of one target's paths. The relative path must be a plain profile-relative
    /// segment (no rooted/drive/UNC/<c>%ENV%</c>/<c>..</c>), and the package-relative source must stay under
    /// the package root — both are re-checked at load so a tampered manifest is rejected before any write.
    /// </summary>
    private static void Validate(MigrationRestoreTarget t)
    {
        ArgumentNullException.ThrowIfNull(t);
        EnsureSafeRelative(t.RelativePath, "destination relative path");
        EnsureSafeRelative(t.PackageRelativeSource, "package-relative source");
    }

    private static void EnsureSafeRelative(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new MigrationManifestException($"{label} is empty");
        if (value.Contains('%'))
            throw new MigrationManifestException($"{label} may not contain an environment token: {value}");
        if (value.Contains(':'))
            throw new MigrationManifestException($"{label} must be relative (drive-qualified rejected): {value}");
        if (value.StartsWith('/') || value.StartsWith('\\'))
            throw new MigrationManifestException($"{label} must be relative (rooted/UNC rejected): {value}");
        foreach (string seg in value.Split('/', '\\'))
            if (seg == "..")
                throw new MigrationManifestException($"{label} may not contain a parent traversal: {value}");
    }
}

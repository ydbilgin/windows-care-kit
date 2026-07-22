using System.Text.Json;
using WindowsCareKit.Core.Abstractions;

namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>The last file written to a migration package: proof that BOTH manifests were committed (NEW-05).</summary>
public sealed record MigrationPackageMarker(
    int SchemaVersion, DateTime CompletedUtc, bool HasInstallManifest, int RestoreTargetCount)
{
    public const string FileName = "migration-package.json";
    public const int CurrentSchemaVersion = 1;
}

/// <summary>JSON read/write for the package completion marker, via the sanctioned write port.</summary>
public sealed class MigrationPackageMarkerStore(IFileWriter writer)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string PathFor(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        return Path.Combine(packageDirectory, MigrationPackageMarker.FileName);
    }

    public void Save(string packageDirectory, MigrationPackageMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        writer.WriteAllText(PathFor(packageDirectory), JsonSerializer.Serialize(marker, JsonOptions));
    }

    public bool Exists(string packageDirectory) => File.Exists(PathFor(packageDirectory));

    public MigrationPackageMarker? TryLoad(string packageDirectory)
    {
        string path = PathFor(packageDirectory);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<MigrationPackageMarker>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

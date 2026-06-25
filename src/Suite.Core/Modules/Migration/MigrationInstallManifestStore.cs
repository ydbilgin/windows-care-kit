using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsCareKit.Core.Modules.Install;

namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// STRICT JSON read/write for the package's self-describing <c>migration-install.json</c> (decision §review finding 3
/// + review fix #3). The migration package is UNTRUSTED, so this loader is the opposite of the Kur module's
/// PERMISSIVE <see cref="InstallManifestLoader"/> (which ignores schema version, silently skips incomplete
/// entries, and returns <see cref="InstallManifest.Empty"/> on malformed JSON — correct for the user's OWN
/// authored <c>90-kurulum.json</c>, WRONG for a package that could be tampered with):
/// <list type="bullet">
/// <item><see cref="JsonUnmappedMemberHandling.Disallow"/> — any unknown JSON field FAILS the load (no silent drop);</item>
/// <item>an unknown <c>schemaVersion</c> is rejected;</item>
/// <item>every entry's method is re-validated against the closed set, exactly-one-locator is re-checked, and the
///   winget id / npm name are re-validated through the SAME <see cref="InstallPlanner"/> allow-lists;</item>
/// <item>a present-but-malformed file THROWS <see cref="MigrationManifestException"/> (never a silent empty success).</item>
/// </list>
/// An ABSENT file returns <see cref="InstallManifest.Empty"/> — a package may legitimately carry no installable
/// apps (config-only migration). The migration package install manifest is loaded ONLY through here; the
/// permissive loader is NEVER routed package data.
/// </summary>
public sealed class MigrationInstallManifestStore
{
    /// <summary>The package file name written/read at the package root.</summary>
    public const string FileName = "migration-install.json";

    /// <summary>The only install-manifest schema version this store reads/writes.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // STRICT read: an unmapped member fails the whole document (the line that makes a smuggled field fail closed).
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>The manifest path for a package directory.</summary>
    public string PathFor(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        return Path.Combine(packageDirectory, FileName);
    }

    /// <summary>
    /// Write the install manifest to the package root in the strict envelope (<c>{ schemaVersion, entries }</c>,
    /// camelCase). An authorized-but-no-install backup writes a valid EMPTY manifest (entries = []).
    /// </summary>
    public void Save(string packageDirectory, IReadOnlyList<InstallEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentNullException.ThrowIfNull(entries);

        var dto = new ManifestDto
        {
            SchemaVersion = CurrentSchemaVersion,
            Entries = entries.Select(ToDto).ToList(),
        };

        Directory.CreateDirectory(packageDirectory);
        string json = JsonSerializer.Serialize(dto, WriteOptions);
        File.WriteAllText(PathFor(packageDirectory), json);
    }

    /// <summary>
    /// Load + STRICTLY validate the install manifest from a package root. An ABSENT file returns
    /// <see cref="InstallManifest.Empty"/>; a present file that is malformed, of an unknown schema version, has an
    /// unknown field, or carries an invalid entry throws <see cref="MigrationManifestException"/>.
    /// </summary>
    public InstallManifest Load(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        string path = PathFor(packageDirectory);

        // A package may legitimately carry no installable apps — absent file is not an error.
        if (!File.Exists(path))
            return InstallManifest.Empty;

        ManifestDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(path), ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new MigrationManifestException($"install manifest is malformed: {ex.Message}");
        }

        if (dto is null)
            throw new MigrationManifestException("install manifest deserialized to null");
        if (dto.SchemaVersion != CurrentSchemaVersion)
            throw new MigrationManifestException($"unsupported install manifest schema version {dto.SchemaVersion}");

        var entries = new List<InstallEntry>();
        foreach (EntryDto e in dto.Entries ?? new List<EntryDto>())
            entries.Add(Validate(e));

        return new InstallManifest(entries);
    }

    /// <summary>
    /// Re-validate one entry fail-closed (the package is untrusted — the loader's checks are not assumed): the id
    /// is non-empty, the method is one of the closed set, EXACTLY ONE locator is present AND matches the method,
    /// and the winget id / npm name pass the SAME <see cref="InstallPlanner"/> allow-lists. A violation throws.
    /// </summary>
    private static InstallEntry Validate(EntryDto e)
    {
        if (string.IsNullOrWhiteSpace(e.Id))
            throw new MigrationManifestException("install entry is missing its id");

        string method = e.Method ?? string.Empty;
        bool known = method is InstallMethod.Winget or InstallMethod.Npm or InstallMethod.UrlManual;
        if (!known)
            throw new MigrationManifestException($"install entry '{e.Id}' has an unknown method '{method}'");

        // EXACTLY ONE locator overall — never two commands, never the wrong locator for the method.
        int locators = (string.IsNullOrWhiteSpace(e.WingetId) ? 0 : 1)
                     + (string.IsNullOrWhiteSpace(e.NpmPackage) ? 0 : 1)
                     + (string.IsNullOrWhiteSpace(e.ManualUrl) ? 0 : 1);
        if (locators != 1)
            throw new MigrationManifestException(
                $"install entry '{e.Id}' must declare EXACTLY ONE locator; found {locators}");

        switch (method)
        {
            case InstallMethod.Winget:
                if (!InstallPlanner.IsValidWingetId(e.WingetId))
                    throw new MigrationManifestException($"install entry '{e.Id}' has an invalid winget id '{e.WingetId}'");
                break;
            case InstallMethod.Npm:
                if (!InstallPlanner.IsValidNpmPackage(e.NpmPackage))
                    throw new MigrationManifestException($"install entry '{e.Id}' has an invalid npm package '{e.NpmPackage}'");
                break;
            case InstallMethod.UrlManual:
                if (string.IsNullOrWhiteSpace(e.ManualUrl))
                    throw new MigrationManifestException($"install entry '{e.Id}' (url-manual) requires a manualUrl");
                break;
        }

        return new InstallEntry(
            Id: e.Id.Trim(),
            Phase: string.IsNullOrWhiteSpace(e.Phase) ? "install" : e.Phase.Trim(),
            Category: e.Category ?? string.Empty,
            Method: method,
            WingetId: NullIfBlank(e.WingetId),
            NpmPackage: NullIfBlank(e.NpmPackage),
            RequiresAdmin: e.RequiresAdmin,
            RebootExpected: e.RebootExpected,
            RestoreOrder: e.RestoreOrder,
            Description: e.Description ?? string.Empty)
        {
            InstallTier = NullIfBlank(e.InstallTier) ?? InstallTier.Auto,
            ManualUrl = NullIfBlank(e.ManualUrl),
        };
    }

    private static EntryDto ToDto(InstallEntry e) => new()
    {
        Id = e.Id,
        Phase = e.Phase,
        Category = e.Category,
        Method = e.Method,
        WingetId = e.WingetId,
        NpmPackage = e.NpmPackage,
        RequiresAdmin = e.RequiresAdmin,
        RebootExpected = e.RebootExpected,
        RestoreOrder = e.RestoreOrder,
        Description = e.Description,
        InstallTier = e.InstallTier,
        ManualUrl = e.ManualUrl,
    };

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---- strict envelope DTOs (the ONLY fields the package install manifest may carry) ----

    private sealed class ManifestDto
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("entries")] public List<EntryDto>? Entries { get; set; }
    }

    private sealed class EntryDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("phase")] public string? Phase { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("method")] public string? Method { get; set; }
        [JsonPropertyName("wingetId")] public string? WingetId { get; set; }
        [JsonPropertyName("npmPackage")] public string? NpmPackage { get; set; }
        [JsonPropertyName("requiresAdmin")] public bool RequiresAdmin { get; set; }
        [JsonPropertyName("rebootExpected")] public bool RebootExpected { get; set; }
        [JsonPropertyName("restoreOrder")] public int RestoreOrder { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("installTier")] public string? InstallTier { get; set; }
        [JsonPropertyName("manualUrl")] public string? ManualUrl { get; set; }
    }
}

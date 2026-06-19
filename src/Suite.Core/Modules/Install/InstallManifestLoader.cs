using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsCareKit.Core.Modules.Install;

/// <summary>
/// Parses <c>90-kurulum.json</c> into a typed <see cref="InstallManifest"/> and assigns each entry a
/// deterministic <see cref="InstallEntry.RestoreOrder"/> from the spec §1.4 restore sequence
/// (driver(net) → winget → core tools → AI CLI → IDE → browser → launcher → WSL → registry → tasks).
/// Pure: it only reads/parses JSON; it emits no plan and touches nothing destructive.
/// </summary>
public sealed class InstallManifestLoader : IInstallManifestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The restore-category order (spec §1.4). The index drives the major sort key; entries inside a
    /// category keep manifest order. Unknown categories sort after all known ones, before tasks/registry.
    /// </summary>
    private static readonly string[] CategoryOrder =
    {
        "ag-surucusu",     // driver (net) — Class=Net only, guarded
        "surucu",          // generic driver alias
        "winget",          // winget core
        "sistem",          // core system tools
        "arac",            // general tools
        "gelistirici",     // developer core (Node lives here → must precede AI CLI)
        "ai-cli",          // AI CLI (npm) — after Node + PATH refresh
        "tarayici",        // browser
        "iletisim",        // communication
        "notlar",          // notes
        "tasarim",         // design
        "oyun-launcher",   // game launchers
        "oyunlar",         // games
        "wsl",             // WSL
        "config",          // config / registry restore
        "registry",
        "gorevler",        // scheduled tasks (last)
    };

    /// <inheritdoc />
    public InstallManifest Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string json = File.ReadAllText(manifestPath);
        return Parse(json);
    }

    /// <inheritdoc />
    public InstallManifest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return InstallManifest.Empty;

        ManifestDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return InstallManifest.Empty;
        }

        if (dto?.Entries is null || dto.Entries.Count == 0)
            return InstallManifest.Empty;

        var entries = new List<InstallEntry>(dto.Entries.Count);
        int sequence = 0;
        foreach (EntryDto e in dto.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Method))
                continue;

            int categoryRank = CategoryRank(e.Category);
            // Restore order = category band (×1000) + manifest position, so the category sequence dominates
            // while order within a category is stable and preserved from the file.
            int restoreOrder = categoryRank * 1000 + sequence;
            sequence++;

            entries.Add(new InstallEntry(
                Id: e.Id.Trim(),
                Phase: e.Phase ?? "install",
                Category: e.Category ?? string.Empty,
                Method: e.Method.Trim(),
                WingetId: NullIfBlank(e.WingetId),
                NpmPackage: NullIfBlank(e.NpmPackage),
                RequiresAdmin: e.RequiresAdmin,
                RebootExpected: e.RebootExpected,
                RestoreOrder: restoreOrder,
                Description: e.Description ?? string.Empty)
            {
                InstallTier = NullIfBlank(e.InstallTier) ?? InstallTier.Auto,
                RequiresNode = e.RequiresNode,
                ManualUrl = NullIfBlank(e.ManualUrl),
                AuthProbe = NullIfBlank(e.AuthProbe),
                AuthKey = NullIfBlank(e.AuthKey),
                AuthCommand = NullIfBlank(e.AuthCommand),
                ConfigSource = NullIfBlank(e.ConfigSource ?? e.Source),
                ConfigDestination = NullIfBlank(e.ConfigDestination ?? e.Target),
            });
        }

        return new InstallManifest(entries);
    }

    private static int CategoryRank(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return CategoryOrder.Length; // unknown → after known categories
        int idx = Array.FindIndex(CategoryOrder, c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx : CategoryOrder.Length;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---- JSON DTOs (mirror the on-disk 90-kurulum.json schema) ----

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
        [JsonPropertyName("installTier")] public string? InstallTier { get; set; }
        [JsonPropertyName("requiresNode")] public bool RequiresNode { get; set; }
        [JsonPropertyName("manualUrl")] public string? ManualUrl { get; set; }
        [JsonPropertyName("authProbe")] public string? AuthProbe { get; set; }
        [JsonPropertyName("authKey")] public string? AuthKey { get; set; }
        [JsonPropertyName("authCommand")] public string? AuthCommand { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        // config-restore fields (forward-compatible — not present in the v1 install manifest)
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("target")] public string? Target { get; set; }
        [JsonPropertyName("configSource")] public string? ConfigSource { get; set; }
        [JsonPropertyName("configDestination")] public string? ConfigDestination { get; set; }
    }
}

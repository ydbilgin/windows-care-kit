using System.IO;
using System.Text.Json;

namespace WindowsCareKit.Core.Modules.Backup;

/// <summary>
/// Default <see cref="IManifestLoader"/>. Reads the ported manifest JSON files (each a
/// <c>{ "entries": [ ... ] }</c> document), maps the schema fields the Backup planner needs, and expands
/// <c>%ENV%</c> in each <c>source</c>. Tolerant of missing optional fields. Unknown <c>method</c> values
/// are preserved verbatim so the planner can classify them (copy vs export-cmd vs install-* vs manual-todo).
/// This is read-only domain logic — it never copies anything (spec §1.3).
/// </summary>
public sealed class ManifestLoader : IManifestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IEnvironmentExpander _expander;

    /// <summary>Creates a loader that expands <c>%ENV%</c> tokens via <paramref name="expander"/>.</summary>
    public ManifestLoader(IEnvironmentExpander expander)
        => _expander = expander ?? throw new ArgumentNullException(nameof(expander));

    /// <inheritdoc />
    public BackupManifest LoadFromDirectory(string manifestsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestsDirectory);
        if (!Directory.Exists(manifestsDirectory))
            return new BackupManifest(Array.Empty<BackupEntry>());

        // Deterministic order: the numeric file-name prefixes (00-, 10-, ...) sort the categories.
        var docs = Directory.EnumerateFiles(manifestsDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileName(f).StartsWith("90-", StringComparison.Ordinal)) // 90-kurulum.json is the Kur install manifest
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .Select(File.ReadAllText);

        return LoadFromJson(docs);
    }

    /// <inheritdoc />
    public BackupManifest LoadFromJson(IEnumerable<string> jsonDocuments)
    {
        ArgumentNullException.ThrowIfNull(jsonDocuments);
        var entries = new List<BackupEntry>();

        foreach (string json in jsonDocuments)
        {
            if (string.IsNullOrWhiteSpace(json))
                continue;

            ManifestFile? file;
            try
            {
                file = JsonSerializer.Deserialize<ManifestFile>(json, JsonOptions);
            }
            catch (JsonException)
            {
                continue; // a malformed manifest file is skipped, not fatal — the others still load
            }

            if (file?.Entries is null)
                continue;

            foreach (RawEntry raw in file.Entries)
                entries.Add(Map(raw));
        }

        return new BackupManifest(entries);
    }

    private BackupEntry Map(RawEntry raw)
    {
        string source = string.IsNullOrEmpty(raw.Source) ? string.Empty : _expander.Expand(raw.Source);
        var forbidden = raw.ForbiddenSources is { } fs
            ? fs.Where(p => !string.IsNullOrWhiteSpace(p)).Select(_expander.Expand).ToArray()
            : Array.Empty<string>();
        return new BackupEntry(
            Id: raw.Id ?? string.Empty,
            Enabled: raw.Enabled ?? false,
            Method: raw.Method ?? string.Empty,
            Category: raw.Category ?? string.Empty,
            Source: source,
            Target: raw.Target ?? string.Empty,
            Exclude: raw.Exclude is { } ex ? ex : Array.Empty<string>(),
            SecretHandling: raw.SecretHandling ?? SecretHandling.Normal,
            RestoreOrder: raw.Restore?.Order ?? 0,
            RestoreMode: raw.Restore?.Mode ?? string.Empty,
            Description: raw.Description ?? string.Empty,
            UiWarning: string.IsNullOrWhiteSpace(raw.UiWarning) ? null : raw.UiWarning)
        {
            ForbiddenSources = forbidden,
            Include = raw.Include is { } inc ? inc.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray() : Array.Empty<string>(),
        };
    }

    // ---- JSON DTOs (match the manifest schema field names; case-insensitive) -----------------

    private sealed class ManifestFile
    {
        public List<RawEntry>? Entries { get; init; }
    }

    private sealed class RawEntry
    {
        public string? Id { get; init; }
        public bool? Enabled { get; init; }
        public string? Method { get; init; }
        public string? Category { get; init; }
        public string? Source { get; init; }
        public string? Target { get; init; }
        public List<string>? Exclude { get; init; }
        public List<string>? ForbiddenSources { get; init; }
        public List<string>? Include { get; init; }
        public string? SecretHandling { get; init; }
        public string? Description { get; init; }
        public string? UiWarning { get; init; }
        public RawRestore? Restore { get; init; }
    }

    private sealed class RawRestore
    {
        public int? Order { get; init; }
        public string? Mode { get; init; }
    }
}

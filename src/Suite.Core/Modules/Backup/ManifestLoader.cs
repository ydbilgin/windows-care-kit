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
    public BackupManifestLoadResult LoadFromDirectory(string manifestsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestsDirectory);
        string[] paths;
        try
        {
            // Materialize inside the try: enumeration is deferred and can fail after EnumerateFiles returns.
            paths = Directory.EnumerateFiles(manifestsDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith("90-", StringComparison.Ordinal))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return new(new BackupManifest(Array.Empty<BackupEntry>()), BackupManifestLoadStatus.NotInstalled,
                Array.Empty<BackupManifestFileOutcome>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(new BackupManifest(Array.Empty<BackupEntry>()), BackupManifestLoadStatus.Unavailable,
                [new(manifestsDirectory, BackupManifestFileStatus.Unreadable, ex.GetType().Name)]);
        }

        var entries = new List<BackupEntry>();
        var outcomes = new List<BackupManifestFileOutcome>(paths.Length);
        foreach (string path in paths)
        {
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcomes.Add(new(path, BackupManifestFileStatus.Unreadable, ex.GetType().Name));
                continue;
            }

            ParseDocument(json, path, entries, outcomes);
        }

        return BuildResult(entries, outcomes);
    }

    /// <inheritdoc />
    public BackupManifestLoadResult LoadFromJson(IEnumerable<string> jsonDocuments)
    {
        ArgumentNullException.ThrowIfNull(jsonDocuments);
        var entries = new List<BackupEntry>();
        var outcomes = new List<BackupManifestFileOutcome>();
        int index = 0;

        foreach (string json in jsonDocuments)
        {
            index++;
            ParseDocument(json, $"<memory:{index}>", entries, outcomes);
        }

        return BuildResult(entries, outcomes);
    }

    private void ParseDocument(
        string json,
        string path,
        List<BackupEntry> entries,
        List<BackupManifestFileOutcome> outcomes)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            outcomes.Add(new(path, BackupManifestFileStatus.Malformed, "BlankDocument"));
            return;
        }

        ManifestFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ManifestFile>(json, JsonOptions);
        }
        catch (JsonException)
        {
            outcomes.Add(new(path, BackupManifestFileStatus.Malformed, nameof(JsonException)));
            return;
        }

        if (file?.Entries is not null)
        {
            foreach (RawEntry raw in file.Entries)
                entries.Add(Map(raw));
        }

        outcomes.Add(new(path, BackupManifestFileStatus.Loaded, null));
    }

    private static BackupManifestLoadResult BuildResult(
        List<BackupEntry> entries,
        List<BackupManifestFileOutcome> outcomes)
    {
        int failures = outcomes.Count(o => o.Status != BackupManifestFileStatus.Loaded);
        int loaded = outcomes.Count - failures;
        BackupManifestLoadStatus status = failures == 0
            ? BackupManifestLoadStatus.Complete
            : loaded == 0
                ? BackupManifestLoadStatus.Unavailable
                : BackupManifestLoadStatus.Partial;
        return new(new BackupManifest(entries), status, outcomes);
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

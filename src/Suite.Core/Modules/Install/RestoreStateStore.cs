using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsCareKit.Core.Modules.Install;

/// <summary>
/// JSON-backed <see cref="IRestoreStateStore"/> writing <c>.kurulum_state.json</c>. Saves use a sibling temp
/// file and <see cref="File.Replace(string,string,string?)"/> so a crash cannot lose an existing journal.
/// First writes create an empty placeholder and replace it, mirroring the copy adapter's atomic write pattern.
/// </summary>
public sealed class RestoreStateStore : IRestoreStateStore
{
    /// <summary>The fixed checkpoint file name (spec §1.4).</summary>
    public const string FileName = ".kurulum_state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public string PathFor(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        return Path.Combine(stateDirectory, FileName);
    }

    /// <inheritdoc />
    public RestoreState Load(string stateDirectory)
    {
        string path = PathFor(stateDirectory);
        if (!File.Exists(path))
            return RestoreState.Empty;

        try
        {
            string json = File.ReadAllText(path);
            StateDto? dto = JsonSerializer.Deserialize<StateDto>(json, JsonOptions);
            if (dto is null)
                return RestoreState.Empty;

            var entries = (dto.Entries ?? new List<EntryDto>())
                .Where(e => !string.IsNullOrWhiteSpace(e.EntryId))
                .Select(e => new RestoreEntryState(e.EntryId!, e.Status))
                .ToArray();

            var journal = (dto.Journal ?? new List<JournalDto>())
                .Where(e => !string.IsNullOrWhiteSpace(e.EntryId) && !string.IsNullOrWhiteSpace(e.TargetPath))
                .Select(e => new RestoreJournalEntry(
                    e.EntryId!,
                    e.TargetPath!,
                    NullIfBlank(e.BakPath),
                    NullIfBlank(e.ShaBefore),
                    NullIfBlank(e.ShaAfter),
                    e.AppliedUtc))
                .ToArray();

            return new RestoreState(dto.PlanHash ?? string.Empty, dto.StartedUtc, dto.UpdatedUtc, entries)
            {
                PackageSha = dto.PackageSha ?? string.Empty,
                Journal = journal,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable checkpoint fails safe to "no checkpoint" → the user starts over.
            return RestoreState.Empty;
        }
    }

    /// <inheritdoc />
    public void Save(string stateDirectory, RestoreState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentNullException.ThrowIfNull(state);

        Directory.CreateDirectory(stateDirectory);

        var dto = new StateDto
        {
            PlanHash = state.PlanHash,
            PackageSha = state.PackageSha,
            StartedUtc = state.StartedUtc,
            UpdatedUtc = state.UpdatedUtc,
            Entries = state.Entries.Select(e => new EntryDto { EntryId = e.EntryId, Status = e.Status }).ToList(),
            Journal = state.Journal.Select(e => new JournalDto
            {
                EntryId = e.EntryId,
                TargetPath = e.TargetPath,
                BakPath = e.BakPath,
                ShaBefore = e.ShaBefore,
                ShaAfter = e.ShaAfter,
                AppliedUtc = e.AppliedUtc,
            }).ToList(),
        };

        string json = JsonSerializer.Serialize(dto, JsonOptions);
        AtomicWrite(PathFor(stateDirectory), json);
    }

    private static void AtomicWrite(string path, string json)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string staging = path + ".wcktmp";
        File.WriteAllText(staging, json);

        if (!File.Exists(path))
            using (File.Create(path)) { }

        File.Replace(staging, path, destinationBackupFileName: null);
    }

    // ---- JSON DTOs ----

    private sealed class StateDto
    {
        public string? PlanHash { get; set; }
        public string? PackageSha { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public List<EntryDto>? Entries { get; set; }
        public List<JournalDto>? Journal { get; set; }
    }

    private sealed class EntryDto
    {
        public string? EntryId { get; set; }
        public RestoreEntryStatus Status { get; set; }
    }

    private sealed class JournalDto
    {
        public string? EntryId { get; set; }
        public string? TargetPath { get; set; }
        public string? BakPath { get; set; }
        public string? ShaBefore { get; set; }
        public string? ShaAfter { get; set; }
        public DateTime AppliedUtc { get; set; }
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

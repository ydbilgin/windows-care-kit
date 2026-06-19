using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Backup;

/// <summary>The integrity manifest file name written into the payload root (spec §1.3).</summary>
public static class BackupIntegrityFiles
{
    /// <summary>The machine-readable per-leaf integrity manifest (destination-relative path + sha256 + size + time).</summary>
    public const string Integrity = "backup_integrity.json";
}

/// <summary>
/// Default <see cref="IIntegrityWriter"/>. After a backup run it walks the DESTINATION tree of every
/// successfully copied entry and records one <see cref="BackupIntegrity"/> per leaf file
/// (destination-relative path + SHA-256 + byte size + copied-at). It records nothing about the source, so
/// the emitted <c>backup_integrity.json</c> carries no username/profile path off-machine (locked decision #4).
///
/// <para><see cref="BuildIntegrity"/> is pure: it touches the world only through the injected
/// <see cref="IFileSystem"/>, <see cref="IHasher"/> and <see cref="IClock"/>, so it is fully unit-testable
/// with in-memory fakes (zero real IO). <see cref="WriteIntegrity"/> mirrors
/// <see cref="BackupReportWriter.WriteReports"/> exactly: it re-evaluates the payload root through the gate as
/// a synthetic <see cref="CopyAction"/> before any write, then <see cref="Directory.CreateDirectory"/> +
/// <see cref="File.WriteAllText"/> (neither API is banned). The integrity step itself never creates a new
/// gated action — it only reads and writes (invariant, locked decision #5).</para>
/// </summary>
public sealed class BackupIntegrityWriter : IIntegrityWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <inheritdoc />
    public IReadOnlyList<BackupIntegrity> BuildIntegrity(
        CopySkipReport copied, string payloadRoot, IFileSystem fs, IHasher hasher, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(copied);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(clock);

        DateTime now = clock.UtcNow;
        var rows = new List<BackupIntegrity>();

        foreach (CopyFileOutcome outcome in copied.Copied)
        {
            string destination = outcome.Destination;
            if (string.IsNullOrWhiteSpace(destination))
                continue;

            // A tree copy lands a directory; a single-file copy lands one file. In both cases hash the LEAF
            // file(s) that actually exist at the destination (per-leaf granularity, locked decision #2).
            if (fs.DirectoryExists(destination))
            {
                foreach (string leaf in fs.EnumerateFiles(destination, recursive: true))
                    rows.Add(Row(outcome.EntryId, leaf, payloadRoot, fs, hasher, now));
            }
            else if (fs.FileExists(destination))
            {
                rows.Add(Row(outcome.EntryId, destination, payloadRoot, fs, hasher, now));
            }
            // If neither exists at integrity time (e.g. removed underneath us) the entry contributes no row;
            // integrity reflects only what is actually on the backup media.
        }

        return rows;
    }

    /// <inheritdoc />
    public string WriteIntegrity(IReadOnlyList<BackupIntegrity> rows, string payloadRoot, ISafetyGate gate)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        ArgumentNullException.ThrowIfNull(gate);

        // Gate the destination before any write — the SAME synthetic-CopyAction probe BackupReportWriter uses,
        // so the integrity manifest is judged by the identical write-target policy and can never be dropped into
        // a protected/system location. This probe is local to the write and is NOT added to any executed plan,
        // so the integrity step produces no new gated action (invariant, locked decision #5).
        var probe = new CopyAction
        {
            Source = payloadRoot,
            Destination = payloadRoot,
            Description = "write backup integrity manifest",
            Reason = "integrity output location",
        };
        SafetyVerdict verdict = gate.Evaluate(probe);
        if (!verdict.Allowed)
            throw new UnauthorizedAccessException(
                $"integrity output location refused by the safety gate: {verdict.Reason}");

        Directory.CreateDirectory(payloadRoot);

        string path = Path.Combine(payloadRoot, BackupIntegrityFiles.Integrity);
        File.WriteAllText(path, JsonSerializer.Serialize(rows, JsonOptions));
        return path;
    }

    private static BackupIntegrity Row(
        string entryId, string leafPath, string payloadRoot, IFileSystem fs, IHasher hasher, DateTime now)
    {
        string relative = ToRelative(payloadRoot, leafPath);
        string sha = hasher.ComputeFileSha256(leafPath);
        long size = SizeOf(fs, leafPath);
        return new BackupIntegrity(entryId, relative, sha, size, now);
    }

    /// <summary>
    /// The destination leaf path made relative to the payload root (no source path is ever recorded). The
    /// result MUST stay under the payload root: if it comes back rooted (a different volume / unrelated path)
    /// or escapes the root via <c>..</c>, the destination is off-root/malformed and we fail closed with
    /// <see cref="OffRootDestinationException"/> rather than silently writing the raw absolute leaf (F3).
    /// </summary>
    private static string ToRelative(string payloadRoot, string leafPath)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(payloadRoot, leafPath);
        }
        catch (ArgumentException ex)
        {
            throw new OffRootDestinationException(
                $"integrity leaf could not be made relative to the payload root: {ex.Message}");
        }

        // GetRelativePath returns the original (rooted) path unchanged when the two have no common root, and
        // can return a "..\…" path when the leaf sits above/outside the root. Either case means the leaf is
        // not actually inside the payload → reject it (fail closed) instead of recording an off-root path.
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new OffRootDestinationException(
                $"integrity leaf is outside the payload root and was refused (fail-closed).");
        }

        return relative;
    }

    /// <summary>Read the leaf's byte size through the read-only port (no <c>FileInfo</c>, no direct IO).</summary>
    private static long SizeOf(IFileSystem fs, string leafPath)
    {
        using Stream stream = fs.OpenRead(leafPath);
        return stream.Length;
    }
}

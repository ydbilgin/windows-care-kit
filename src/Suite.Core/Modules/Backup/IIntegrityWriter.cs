using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Backup;

/// <summary>
/// Builds and writes the post-copy integrity manifest (<c>backup_integrity.json</c>) for a backup run
/// (spec §1.3). Building is a pure, in-memory walk of the destination tree (testable with fakes, zero real
/// IO); writing mirrors <see cref="BackupReportWriter.WriteReports"/> exactly — it re-gates the payload root
/// through the <see cref="ISafetyGate"/> before touching disk and never produces a new gated action.
/// </summary>
public interface IIntegrityWriter
{
    /// <summary>
    /// For every leaf file under <paramref name="payloadRoot"/> that belongs to a successfully copied entry,
    /// produce one <see cref="BackupIntegrity"/> row (destination-relative path + SHA-256 + byte size +
    /// copied-at). Pure: it enumerates and reads only through <paramref name="fs"/>/<paramref name="hasher"/>,
    /// stamps the time from <paramref name="clock"/>, and never writes.
    /// </summary>
    IReadOnlyList<BackupIntegrity> BuildIntegrity(
        CopySkipReport copied, string payloadRoot, IFileSystem fs, IHasher hasher, IClock clock);

    /// <summary>
    /// Write <paramref name="rows"/> as <c>backup_integrity.json</c> into <paramref name="payloadRoot"/>.
    /// Re-gates the destination through <paramref name="gate"/> first (same synthetic-CopyAction probe the
    /// report writer uses); throws <see cref="UnauthorizedAccessException"/> when the gate blocks it. Returns
    /// the written file path.
    /// </summary>
    string WriteIntegrity(IReadOnlyList<BackupIntegrity> rows, string payloadRoot, ISafetyGate gate);
}

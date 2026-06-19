namespace WindowsCareKit.Core.Modules.Backup;

/// <summary>Why a single copy entry did not complete — surfaced in the skip-report, never silently swallowed (spec §1.3).</summary>
public enum CopySkipReason
{
    /// <summary>The source/destination was locked or in use (sharing violation that retries did not clear).</summary>
    Locked,

    /// <summary>The source is a protected secret store the engine refuses to read (DPAPI / password DB).</summary>
    Forbidden,

    /// <summary>The path exceeded what the OS could handle even with long-path support.</summary>
    TooLong,

    /// <summary>The source did not exist at execution time.</summary>
    Missing,

    /// <summary>The gate re-evaluated the destination as blocked at execution time.</summary>
    Blocked,

    /// <summary>Any other IO failure surfaced by the copy engine.</summary>
    Other,
}

/// <summary>The result of one planned copy entry after the executor ran it.</summary>
/// <param name="EntryId">The manifest entry id (or copy action id) this outcome is for.</param>
/// <param name="Source">The (expanded) source path.</param>
/// <param name="Destination">The payload destination path.</param>
/// <param name="Copied">True when the copy completed.</param>
/// <param name="Reason">Null when copied; otherwise why it was skipped.</param>
/// <param name="Detail">Human-readable detail from the executor (exception summary, gate reason, …).</param>
public sealed record CopyFileOutcome(
    string EntryId,
    string Source,
    string Destination,
    bool Copied,
    CopySkipReason? Reason,
    string Detail);

/// <summary>
/// The shaped skip-report the Backup domain produces from the raw executor outcomes (spec §1.3). The
/// <c>CopyAdapter</c> performs the IO and the executor records per-action results; this collects them into
/// a copied/skipped split that <see cref="BackupReportWriter"/> turns into <c>RAPOR.md</c>.
/// </summary>
/// <param name="Outcomes">One outcome per planned copy entry, in plan order.</param>
public sealed record CopySkipReport(IReadOnlyList<CopyFileOutcome> Outcomes)
{
    /// <summary>The entries that copied successfully.</summary>
    public IReadOnlyList<CopyFileOutcome> Copied
        => Outcomes.Where(o => o.Copied).ToArray();

    /// <summary>The entries that did not copy (locked / forbidden / too-long / missing / blocked).</summary>
    public IReadOnlyList<CopyFileOutcome> Skipped
        => Outcomes.Where(o => !o.Copied).ToArray();

    /// <summary>An empty report (no copies were planned).</summary>
    public static CopySkipReport Empty { get; } = new(Array.Empty<CopyFileOutcome>());
}

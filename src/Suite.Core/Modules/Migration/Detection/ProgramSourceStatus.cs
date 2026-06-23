namespace WindowsCareKit.Core.Modules.Migration.Detection;

/// <summary>
/// Per-source health indicator (B-5 / B.3 non-vacuous rule). Used in <see cref="ProgramSourceReport"/>
/// to give the UI an honest, per-source status.
/// </summary>
public enum ProgramSourceStatus
{
    /// <summary>Source enumerated successfully and returned at least one record.</summary>
    Ok,

    /// <summary>Source is structurally unavailable on this machine (e.g., AppX on a Server SKU).</summary>
    SourceUnavailable,

    /// <summary>Source returned partial results (budget-limited or access-denied on some entries).</summary>
    Incomplete,

    /// <summary>
    /// Source threw an exception, or returned an empty list where non-empty is expected (B.3 rule:
    /// the real uninstall hive is never empty on a live machine).
    /// </summary>
    SourceFailed,
}

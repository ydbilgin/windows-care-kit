namespace WindowsCareKit.Core.Modules.Migration.Detection;

/// <summary>
/// A pluggable program inventory source. Each source enumerates one kind of program registry/catalog,
/// projects entries into normalized <see cref="DiscoveredProgram"/> records, and reports its own status.
/// </summary>
public interface IProgramSource
{
    /// <summary>Which catalog this source reads.</summary>
    ProgramSourceKind Kind { get; }

    /// <summary>
    /// Enumerates programs from this source. Must never throw — exceptions are caught internally and
    /// returned as <see cref="ProgramSourceStatus.SourceFailed"/> in the report.
    /// </summary>
    ProgramEnumeration Enumerate();
}

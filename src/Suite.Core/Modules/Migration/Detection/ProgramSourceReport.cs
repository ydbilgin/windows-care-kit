namespace WindowsCareKit.Core.Modules.Migration.Detection;

/// <summary>Per-source status summary surfaced to the UI (D.1).</summary>
/// <param name="Kind">Which source this report describes.</param>
/// <param name="Status">Health of the enumeration.</param>
/// <param name="Count">Number of programs yielded by this source (0 when status is not Ok).</param>
/// <param name="Detail">Optional human-readable status detail for honest scan reporting.</param>
public sealed record ProgramSourceReport(ProgramSourceKind Kind, ProgramSourceStatus Status, int Count, string? Detail = null);

/// <summary>The full output of one source enumeration: the programs it yielded and its own status report.</summary>
/// <param name="Programs">Projected <see cref="DiscoveredProgram"/> list (may be empty on failure).</param>
/// <param name="Report">Per-source health.</param>
public sealed record ProgramEnumeration(IReadOnlyList<DiscoveredProgram> Programs, ProgramSourceReport Report);

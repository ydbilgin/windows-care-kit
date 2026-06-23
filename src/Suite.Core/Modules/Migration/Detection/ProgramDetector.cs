namespace WindowsCareKit.Core.Modules.Migration.Detection;

/// <summary>
/// The full detection result: merged, deduped programs from all sources, plus per-source status reports.
/// </summary>
/// <param name="Programs">Deduped, sorted program list.</param>
/// <param name="SourceReports">One report per source that was invoked, in enumeration order.</param>
public sealed record DetectionResult(
    IReadOnlyList<DiscoveredProgram> Programs,
    IReadOnlyList<ProgramSourceReport> SourceReports);

/// <summary>
/// Orchestrates one or more <see cref="IProgramSource"/> instances: enumerates each in constructor
/// order, merges via <see cref="ProgramDedupLayer"/>, and surfaces per-source status.
/// </summary>
public sealed class ProgramDetector
{
    private readonly IReadOnlyList<IProgramSource> _sources;

    public ProgramDetector(IEnumerable<IProgramSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = [.. sources];
    }

    /// <summary>Run all sources, dedup, and return the merged result.</summary>
    public DetectionResult Detect()
    {
        var allPrograms = new List<DiscoveredProgram>();
        var reports = new List<ProgramSourceReport>(_sources.Count);

        foreach (var source in _sources)
        {
            var enumeration = source.Enumerate();
            allPrograms.AddRange(enumeration.Programs);
            reports.Add(enumeration.Report);
        }

        var merged = ProgramDedupLayer.Merge(allPrograms);
        return new DetectionResult(merged, reports);
    }
}

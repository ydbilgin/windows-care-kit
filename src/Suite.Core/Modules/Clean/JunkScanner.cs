using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Clean;

/// <summary>
/// The output of a read-only junk scan: a gate-approved dry-run plan plus what the gate refused.
/// Mirrors <see cref="LeftoverScanResult"/> so the UI can reuse the same row pattern.
/// </summary>
public sealed record JunkScanResult(OperationPlan Plan, IReadOnlyList<SkippedAction> Skipped);

/// <summary>
/// Turns the junk folders found by an <see cref="IJunkProbe"/> into a typed, risk-classified dry-run
/// <see cref="OperationPlan"/> of recycle-bin <see cref="FileDeleteAction"/>s. Every candidate passes
/// through the <see cref="ISafetyGate"/>: protected folders never enter the plan — they are reported
/// as <see cref="SkippedAction"/> instead. The scan is read-only; nothing is deleted (spec §1.2).
/// Junk deletes are <see cref="RiskLevel.Low"/> + <see cref="UndoCapability.Full"/> (recycle bin), so
/// the executor treats them as best-effort (§A.4): one locked temp file does not abort the sweep.
/// </summary>
public sealed class JunkScanner
{
    private readonly IJunkProbe _probe;
    private readonly ISafetyGate _gate;

    public JunkScanner(IJunkProbe probe, ISafetyGate gate)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <summary>Build the dry-run plan from the probe's candidates, gate-filtered.</summary>
    public JunkScanResult Scan(DateTime utc)
    {
        var candidates = new List<PlannedAction>();

        foreach (JunkCandidate c in _probe.FindJunk())
        {
            if (string.IsNullOrWhiteSpace(c.Path))
                continue;

            candidates.Add(new FileDeleteAction
            {
                Path = c.Path,
                ToRecycleBin = true,
                Description = $"Delete junk folder: {c.Path}",
                Reason = c.ApproxBytes > 0 ? $"{c.Note} (~{FormatBytes(c.ApproxBytes)})" : c.Note,
                Risk = RiskLevel.Low,
                Undo = UndoCapability.Full, // recycle bin
                BestEffort = true,          // junk sweep: a single locked temp file must not abort the cleanup (§A.4)
            });
        }

        var allowed = new List<PlannedAction>();
        var skipped = new List<SkippedAction>();
        foreach (PlannedAction action in candidates)
        {
            SafetyVerdict verdict = _gate.Evaluate(action);
            if (verdict.Allowed)
                allowed.Add(action);
            else
                skipped.Add(new SkippedAction(action, verdict.Reason));
        }

        var plan = new OperationPlan("Clean junk and temporary files", "clean", allowed, utc);
        return new JunkScanResult(plan, skipped);
    }

    /// <summary>Compact human size for the row note (KB/MB/GB, no external dependency).</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{(long)size} {units[unit]}"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{size:0.#} {units[unit]}");
    }
}

using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Install;

/// <summary>
/// The result of an install-plan export run: whether the payload root was authorized for the write, and the
/// document that was produced. On a refusal nothing is written and <see cref="Export"/> still carries the
/// (computed-but-unwritten) document so the UI can explain what would have been exported.
/// </summary>
/// <param name="Authorized">False when the gate refused the payload root (nothing was written).</param>
/// <param name="Export">The classified export document built from the plan.</param>
public sealed record InstallRunResult(bool Authorized, InstallPlanExportDoc Export);

/// <summary>
/// The pure, headless orchestrator for the Install/Restore EXPORT slice (Step 3) — the mirror of
/// <c>BackupRunner</c> as a thin orchestrator. <see cref="ExportPlan"/> projects an already-built
/// <see cref="InstallPlanResult"/> into an <see cref="InstallPlanExportDoc"/> (<see cref="InstallPlanExport.Build"/>)
/// and writes <c>install_plan.json</c> via the <see cref="IInstallPlanWriter"/> (which re-gates the payload root).
/// It is a DRY-RUN: it reads the plan and writes JSON only, and produces no new gated action (the writer's single
/// write-target probe is the only gate evaluation — invariant, locked decision #6). A refusal writes nothing.
/// </summary>
public sealed class InstallRunner
{
    private readonly IInstallPlanWriter _writer;
    private readonly IClock _clock;

    public InstallRunner(IInstallPlanWriter writer, IClock clock)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Build the export document from <paramref name="result"/> and write <c>install_plan.json</c> into
    /// <paramref name="payloadRoot"/>. The writer re-gates the payload root first; when the gate refuses it the
    /// writer throws and this method surfaces an unauthorized result WITHOUT writing anything. This is a dry-run:
    /// no execution seam is invoked (the runner reads the plan and writes JSON).
    /// </summary>
    public InstallRunResult ExportPlan(
        InstallPlanResult result, string payloadRoot, ISafetyGate gate)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        ArgumentNullException.ThrowIfNull(gate);

        InstallPlanExportDoc doc = InstallPlanExport.Build(result, _clock);

        try
        {
            _writer.WriteExport(doc, payloadRoot, gate);
        }
        catch (UnauthorizedAccessException)
        {
            // The gate refused the payload root: nothing was written. Report the refusal; do not rethrow so the
            // UI can present "export refused" rather than crash. The (unwritten) doc is still returned for context.
            return new InstallRunResult(false, doc);
        }

        return new InstallRunResult(true, doc);
    }
}

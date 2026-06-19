using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Install;

/// <summary>
/// Writes the exported install plan (<c>install_plan.json</c>) into the payload root (Step 3 EXPORT slice).
/// Mirrors <c>IIntegrityWriter.WriteIntegrity</c> exactly: it re-gates the payload root through the
/// <see cref="ISafetyGate"/> before touching disk (same synthetic-CopyAction write-target probe) and never
/// produces a new gated action — it only reads the already-built document and writes JSON.
/// </summary>
public interface IInstallPlanWriter
{
    /// <summary>
    /// Write <paramref name="doc"/> as <c>install_plan.json</c> into <paramref name="payloadRoot"/>. Re-gates the
    /// destination through <paramref name="gate"/> first; throws <see cref="UnauthorizedAccessException"/> when the
    /// gate blocks it (nothing is written). Returns the written file path.
    /// </summary>
    string WriteExport(InstallPlanExportDoc doc, string payloadRoot, ISafetyGate gate);
}

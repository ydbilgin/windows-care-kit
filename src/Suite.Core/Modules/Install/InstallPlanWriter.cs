using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Install;

/// <summary>The exported install-plan file name written into the payload root (Step 3 EXPORT slice).</summary>
public static class InstallPlanFiles
{
    /// <summary>The machine-readable, host-portable install plan (classified items + schema version + generated-at).</summary>
    public const string Plan = "install_plan.json";
}

/// <summary>
/// Default <see cref="IInstallPlanWriter"/>. Mirrors <c>BackupIntegrityWriter.WriteIntegrity</c> BIREBIR
/// (exactly): it re-evaluates the payload root through the gate as a synthetic <see cref="CopyAction"/>
/// (<c>Source == Destination == payloadRoot</c>) before any write — so the install plan is judged by the
/// identical write-target policy and can never be dropped into a protected/system location — then
/// <see cref="Directory.CreateDirectory"/> + <see cref="File.WriteAllText"/> (neither API is banned). The
/// export step itself produces NO new gated action: the probe is local to the write and is never added to an
/// executed plan (invariant, locked decision #6).
/// </summary>
public sealed class InstallPlanWriter : IInstallPlanWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <inheritdoc />
    public string WriteExport(InstallPlanExportDoc doc, string payloadRoot, ISafetyGate gate)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        ArgumentNullException.ThrowIfNull(gate);

        // Gate the destination before any write — the SAME synthetic-CopyAction probe BackupIntegrityWriter uses,
        // so the install plan is judged by the identical write-target policy and can never be dropped into a
        // protected/system location. This probe is local to the write and is NOT added to any executed plan, so
        // the export step produces no new gated action (invariant, locked decision #6).
        var probe = new CopyAction
        {
            Source = payloadRoot,
            Destination = payloadRoot,
            Description = "write install plan",
            Reason = "install plan output location",
        };
        SafetyVerdict verdict = gate.Evaluate(probe);
        if (!verdict.Allowed)
            throw new UnauthorizedAccessException(
                $"install plan output location refused by the safety gate: {verdict.Reason}");

        Directory.CreateDirectory(payloadRoot);

        string path = Path.Combine(payloadRoot, InstallPlanFiles.Plan);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOptions));
        return path;
    }
}

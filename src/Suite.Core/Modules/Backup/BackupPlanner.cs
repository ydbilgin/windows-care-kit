using System.IO;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Backup;

/// <summary>A manifest entry the planner did not turn into a copy, plus the human reason why.</summary>
/// <param name="Entry">The source manifest entry.</param>
/// <param name="Reason">Why it was skipped (disabled, gate-blocked, never-read secret, …).</param>
public sealed record BackupSkip(BackupEntry Entry, string Reason);

/// <summary>
/// The output of a read-only backup scan: the gate-approved dry-run <see cref="OperationPlan"/> of copies,
/// the items that became manual to-dos (never-read secrets / manual checklist), the items the gate or the
/// planner skipped, and the install-list entries (<c>install-*</c>, never copied — handed to the Kur module).
/// </summary>
/// <param name="Plan">The dry-run plan of <see cref="CopyAction"/>s (empty when nothing is copyable).</param>
/// <param name="ManualTodos">Entries that need a manual step after the format (re-login / checklist).</param>
/// <param name="Skipped">Entries excluded from the plan (disabled, gate-blocked, missing target, …).</param>
/// <param name="ReinstallList">Installer entries (<c>install-*</c>) listed for the Kur reinstall flow, not copied.</param>
public sealed record BackupPlanResult(
    OperationPlan Plan,
    IReadOnlyList<BackupEntry> ManualTodos,
    IReadOnlyList<BackupSkip> Skipped,
    IReadOnlyList<BackupEntry> ReinstallList);

/// <summary>
/// Turns a <see cref="BackupManifest"/> into a typed, gate-approved dry-run <see cref="OperationPlan"/> of
/// <see cref="CopyAction"/>s (spec §1.3). It is read-only: it emits a plan, it never copies. The rules:
/// <list type="bullet">
/// <item>Only enabled <c>method == "copy"</c> entries whose <c>secretHandling</c> is not <c>never-read</c>/
/// <c>manual-only</c> become a <c>CopyAction</c>.</item>
/// <item><c>never-read</c> / <c>manual-todo</c> entries become MANUAL_TODO lines (re-login is the safe path).</item>
/// <item><c>install-*</c> entries are collected into a reinstall list for the Kur module — never a Backup action.</item>
/// <item>Every <c>CopyAction</c> is checked through the <see cref="ISafetyGate"/> on its DESTINATION; a blocked
/// destination is reported as skipped, never copied.</item>
/// <item>The payload root MUST satisfy the injected <see cref="PayloadRootPolicy"/> (spec §1.3) — a refused root
/// yields an empty plan with every entry skipped, so the UI can show the "outside the repo" warning.</item>
/// </list>
/// </summary>
public sealed class BackupPlanner
{
    private readonly ISafetyGate _gate;
    private readonly IEnvironmentExpander _expander;
    private readonly PayloadRootPolicy _payloadRootPolicy;

    /// <summary>Creates a planner that gates copies, expands payload-relative targets, and applies the supplied
    /// payload-root policy.</summary>
    public BackupPlanner(ISafetyGate gate, IEnvironmentExpander expander, PayloadRootPolicy payloadRootPolicy)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _expander = expander ?? throw new ArgumentNullException(nameof(expander));
        _payloadRootPolicy = payloadRootPolicy ?? throw new ArgumentNullException(nameof(payloadRootPolicy));
    }

    /// <summary>
    /// Build the dry-run backup plan. <paramref name="payloadRootDir"/> is the backup output folder; it MUST be
    /// accepted by the injected payload-root policy (spec §1.3). When it is not, the result is an empty plan with
    /// every copyable entry reported as skipped so the UI can surface the payload-location warning.
    /// </summary>
    public BackupPlanResult BuildPlan(BackupManifest manifest, string payloadRootDir, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var copies = new List<PlannedAction>();
        var manual = new List<BackupEntry>();
        var skipped = new List<BackupSkip>();
        var reinstall = new List<BackupEntry>();

        PayloadRootVerdict payloadVerdict = _payloadRootPolicy.Evaluate(payloadRootDir);
        bool payloadValid = payloadVerdict.IsAllowed;
        string normalizedPayload = payloadVerdict.NormalizedRoot;
        string payloadReason = payloadValid ? string.Empty : ReasonFor(payloadVerdict.Rejection);

        foreach (BackupEntry entry in manifest.Entries)
        {
            if (entry.IsInstall)
            {
                reinstall.Add(entry);
                continue;
            }

            if (entry.IsManualTodo)
            {
                manual.Add(entry);
                continue;
            }

            if (!entry.Enabled)
            {
                skipped.Add(new BackupSkip(entry, "disabled (opt-in)"));
                continue;
            }

            // Non-copy methods (export-cmd, unknown) are listed, not actioned, by Backup.
            if (!string.Equals(entry.Method, BackupMethod.Copy, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(new BackupSkip(entry, $"method '{entry.Method}' is not a backup copy"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Target))
            {
                skipped.Add(new BackupSkip(entry, "missing source or target"));
                continue;
            }

            if (!payloadValid)
            {
                skipped.Add(new BackupSkip(entry, payloadReason));
                continue;
            }

            if (!TryCombineTarget(normalizedPayload, entry.Target, out string destination, out string targetReason))
            {
                skipped.Add(new BackupSkip(entry, targetReason));
                continue;
            }
            if (AreNested(entry.Source, destination))
            {
                skipped.Add(new BackupSkip(entry, "backup destination is inside the source being backed up"));
                continue;
            }
            var action = new CopyAction
            {
                Source = entry.Source,
                Destination = destination,
                // Plumb the manifest exclusions AND the include allow-list through the typed action so the
                // copy engine actually enforces them (spec §1.3) — not just the built-in secret-leaf superset.
                ExcludeLeaves = entry.Exclude,
                ForbiddenSources = entry.ForbiddenSources,
                Include = entry.Include,
                Description = $"Copy {entry.Source} → {entry.Target}",
                Reason = string.IsNullOrWhiteSpace(entry.Description) ? entry.Id : entry.Description,
                Risk = RiskLevel.Low,        // a copy is non-destructive
                Undo = UndoCapability.None,  // nothing to undo: it only writes into the (new) payload tree
            };

            SafetyVerdict verdict = _gate.Evaluate(action); // gate runs on CopyAction.Destination
            if (verdict.Allowed)
                copies.Add(action);
            else
                skipped.Add(new BackupSkip(entry, $"safety gate blocked destination: {verdict.Reason}"));
        }

        var plan = new OperationPlan("Back up settings and files", "backup", copies, utc);
        return new BackupPlanResult(plan, manual, skipped, reinstall);
    }

    private static string ReasonFor(PayloadRootRejection rejection) =>
        rejection switch
        {
            PayloadRootRejection.NotProvided => "no backup folder chosen",
            PayloadRootRejection.Unparseable => "backup folder path is invalid",
            PayloadRootRejection.NotLocalDrivePath => "backup folder must be a local drive path",
            PayloadRootRejection.InsideForbiddenRoot => "backup folder must be outside the app folder",
            _ => throw new ArgumentOutOfRangeException(nameof(rejection), rejection, "Unknown payload-root rejection."),
        };

    /// <summary>Combine the payload root with a manifest target and prove the result stays INSIDE the payload
    /// root (rejecting any '..'/reparse escape). Returns false with a reason when the combination escapes.</summary>
    private static bool TryCombineTarget(string payloadRoot, string target, out string destination, out string reason)
    {
        destination = string.Empty;
        reason = string.Empty;
        string relative = target.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        string combined;
        try { combined = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(payloadRoot, relative))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "target path is invalid";
            return false;
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadRoot));
        if (!string.Equals(combined, root, StringComparison.OrdinalIgnoreCase)
            && !combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            reason = "target path escapes the backup payload root";
            return false;
        }
        destination = combined;
        return true;
    }

    /// <summary>True when either path equals or is nested inside the other, canonically. Prevents a destination
    /// placed inside the source being copied into itself (unbounded recursion), and vice versa.</summary>
    private static bool AreNested(string a, string b)
    {
        string na, nb;
        try
        {
            na = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
            nb = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        }
        catch { return false; }
        return na.Equals(nb, StringComparison.OrdinalIgnoreCase)
            || na.StartsWith(nb + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || nb.StartsWith(na + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

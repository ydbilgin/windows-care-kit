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
/// <item>The payload root MUST be outside the app folder (spec §1.3) — an invalid root yields an empty plan
/// with every entry skipped, so the UI can show the "outside the repo" warning.</item>
/// </list>
/// </summary>
public sealed class BackupPlanner
{
    private readonly ISafetyGate _gate;
    private readonly IEnvironmentExpander _expander;

    /// <summary>Creates a planner that gates copies and expands payload-relative targets via <paramref name="expander"/>.</summary>
    public BackupPlanner(ISafetyGate gate, IEnvironmentExpander expander)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _expander = expander ?? throw new ArgumentNullException(nameof(expander));
    }

    /// <summary>
    /// Build the dry-run backup plan. <paramref name="payloadRootDir"/> is the backup output folder; it MUST be
    /// an absolute path outside the application folder (spec §1.3). When it is not, the result is an empty plan
    /// with every copyable entry reported as skipped so the UI can surface the payload-location warning.
    /// </summary>
    public BackupPlanResult BuildPlan(BackupManifest manifest, string payloadRootDir, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var copies = new List<PlannedAction>();
        var manual = new List<BackupEntry>();
        var skipped = new List<BackupSkip>();
        var reinstall = new List<BackupEntry>();

        bool payloadValid = IsValidPayloadRoot(payloadRootDir, out string normalizedPayload, out string payloadReason);

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

            string destination = CombineTarget(normalizedPayload, entry.Target);
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

    /// <summary>
    /// True when <paramref name="payloadRootDir"/> is an absolute, drive-rooted path that is NOT the app folder
    /// or inside it (spec §1.3: the payload must live outside the app). The app folder is
    /// <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    private static bool IsValidPayloadRoot(string? payloadRootDir, out string normalized, out string reason)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(payloadRootDir))
        {
            reason = "no backup folder chosen";
            return false;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadRootDir));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "backup folder path is invalid";
            return false;
        }

        if (full.Length < 2 || !char.IsLetter(full[0]) || full[1] != ':')
        {
            reason = "backup folder must be a local drive path";
            return false;
        }

        string appDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        if (string.Equals(full, appDir, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(appDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            reason = "backup folder must be outside the app folder";
            return false;
        }

        normalized = full;
        reason = string.Empty;
        return true;
    }

    /// <summary>Combine the payload root with a manifest <c>target</c> (which uses forward slashes).</summary>
    private static string CombineTarget(string payloadRoot, string target)
    {
        string relative = target.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(payloadRoot, relative));
    }
}

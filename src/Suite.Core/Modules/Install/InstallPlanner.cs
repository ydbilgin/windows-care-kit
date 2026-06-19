using System.IO;
using System.Text.RegularExpressions;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Install;

/// <summary>Why a manifest entry did not become an executable action (for the honest "what was skipped" list).</summary>
public enum InstallSkipReason
{
    /// <summary>Tier is <c>manual-after</c> (login wall, big download, reboot, UAC) — listed as a manual step.</summary>
    ManualAfter,
    /// <summary>An <c>install-url-manual</c> entry — surfaced as a download link, never auto-run.</summary>
    ManualUrl,
    /// <summary>A driver entry whose class is not <c>Net</c> — spec §1.4 forbids non-network driver restore.</summary>
    DriverNotNet,
    /// <summary>Already completed in the checkpoint (<c>.kurulum_state.json</c>) — resume skips it.</summary>
    AlreadyDone,
    /// <summary>The SafetyGate refused the resulting action (e.g. a denied command).</summary>
    GateBlocked,
    /// <summary>The entry's method/fields were incomplete (e.g. winget with no id) — nothing to run.</summary>
    Incomplete,
}

/// <summary>One manifest entry that did not enter the executable plan, with the reason and a UI note.</summary>
public sealed record InstallSkip(InstallEntry Entry, InstallSkipReason Reason, string Note);

/// <summary>
/// The output of building a restore plan: the gate-approved, ordered <see cref="OperationPlan"/>, the
/// entries that were skipped (with reasons), and the manual-after checklist the UI must show the user.
/// </summary>
public sealed record InstallPlanResult(
    OperationPlan Plan,
    IReadOnlyList<InstallSkip> Skipped,
    IReadOnlyList<InstallEntry> ManualChecklist)
{
    /// <summary>
    /// The authoritative action-id → manifest-entry-id correlation, stamped by the planner at the moment it
    /// built each action from its entry. The checkpoint uses this to mark the right entry done/failed instead
    /// of re-deriving it positionally (which silently misaligns if an entry ever yields zero or several
    /// actions, or the ordering changes — L10).
    /// </summary>
    public IReadOnlyDictionary<string, string> ActionEntryIds { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>
/// Turns the reinstall manifest into a typed, ordered, gate-approved dry-run <see cref="OperationPlan"/>
/// (spec §1.4): <c>install-winget</c>/<c>install-npm</c> → <see cref="CommandAction"/>; config restore →
/// <see cref="RestoreMergeAction"/> (with a timestamped .bak). Actions are ordered by the restore
/// sequence (<see cref="InstallEntry.RestoreOrder"/>). Driver entries enter the plan ONLY when the
/// <see cref="IDriverGuard"/> confirms <c>Class=Net</c>. Already-done entries (from the checkpoint),
/// manual-after entries, and url-manual entries never become executable actions — they are reported.
/// This is read-only: it emits a plan, it never installs anything (execution is <c>GatedExecutor</c>).
/// </summary>
public sealed class InstallPlanner
{
    /// <summary>Categories whose entries are treated as drivers and must pass the <see cref="IDriverGuard"/>.</summary>
    private static readonly HashSet<string> DriverCategories =
        new(StringComparer.OrdinalIgnoreCase) { "ag-surucusu", "surucu", "driver" };

    private readonly ISafetyGate _gate;
    private readonly IDriverGuard _driverGuard;

    public InstallPlanner(ISafetyGate gate, IDriverGuard driverGuard)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _driverGuard = driverGuard ?? throw new ArgumentNullException(nameof(driverGuard));
    }

    /// <summary>Builds the restore plan, skipping entries already <see cref="RestoreEntryStatus.Done"/> in <paramref name="state"/>.</summary>
    public InstallPlanResult BuildPlan(InstallManifest manifest, RestoreState state, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(state);

        var actions = new List<PlannedAction>();
        var skipped = new List<InstallSkip>();
        var manual = new List<InstallEntry>();
        var actionEntryIds = new Dictionary<string, string>();

        // Ordered by the restore sequence; manifest position is the stable tie-break (already in RestoreOrder).
        IEnumerable<InstallEntry> ordered = manifest.Entries.OrderBy(e => e.RestoreOrder);

        foreach (InstallEntry entry in ordered)
        {
            // 1) Resume: a completed entry is skipped without re-planning.
            if (state.IsDone(entry.Id))
            {
                skipped.Add(new InstallSkip(entry, InstallSkipReason.AlreadyDone, "Already installed (checkpoint)"));
                continue;
            }

            // 2) Manual url entries are never auto-run — they go to the checklist.
            if (string.Equals(entry.Method, InstallMethod.UrlManual, StringComparison.OrdinalIgnoreCase))
            {
                manual.Add(entry);
                skipped.Add(new InstallSkip(entry, InstallSkipReason.ManualUrl,
                    entry.ManualUrl is null ? "Manual download" : $"Manual download: {entry.ManualUrl}"));
                continue;
            }

            // 3) manual-after entries (login/UAC/reboot/heavy) are listed, not auto-run.
            if (!entry.IsAutomatable)
            {
                manual.Add(entry);
                skipped.Add(new InstallSkip(entry, InstallSkipReason.ManualAfter, "Run manually after the automatic phase"));
                continue;
            }

            // 4) Driver entries: only Class=Net may be restored (spec §1.4).
            if (DriverCategories.Contains(entry.Category) && !_driverGuard.IsNetClass(entry.WingetId ?? entry.Id))
            {
                skipped.Add(new InstallSkip(entry, InstallSkipReason.DriverNotNet, "Skipped: only network (Class=Net) drivers are restored"));
                continue;
            }

            // 5) Build the typed action for this entry's method.
            PlannedAction? action = BuildAction(entry);
            if (action is null)
            {
                skipped.Add(new InstallSkip(entry, InstallSkipReason.Incomplete, "Entry is missing the data needed to run it"));
                continue;
            }

            // 6) Gate every action — a blocked one is reported, never planned.
            SafetyVerdict verdict = _gate.Evaluate(action);
            if (verdict.Allowed)
            {
                actions.Add(action);
                // Stamp the correlation at build time (action id → entry id) so the checkpoint never has to
                // re-derive it positionally (L10).
                actionEntryIds[action.Id] = entry.Id;
            }
            else
            {
                skipped.Add(new InstallSkip(entry, InstallSkipReason.GateBlocked, verdict.Reason));
            }
        }

        var plan = new OperationPlan("Reinstall apps and restore settings", "install", actions, utc);
        return new InstallPlanResult(plan, skipped, manual) { ActionEntryIds = actionEntryIds };
    }

    // Resolved ONCE to absolute, verified paths so the OS never PATH-searches the interpreter (PATH-hijack
    // defense): winget ships under WindowsApps; npm under the Node install dir (fallbacks below).
    private static readonly string WingetPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WindowsApps", "winget.exe");

    // npm package names only — never a URL, git ref, path, or shorthand (which would run arbitrary code).
    private static readonly Regex NpmPackageName = new(
        @"^(@[a-z0-9][a-z0-9\-._]*\/)?[a-z0-9][a-z0-9\-._]*(@(latest|[~^]?[0-9][0-9a-z\-.+]*))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // winget package ids only (allow-list, the counterpart of NpmPackageName): a publisher.product id made of
    // alphanumerics plus '.' '+' '_' '-'. It MUST start with an alphanumeric so a crafted manifest cannot pass
    // a leading '-' that winget would read as an extra flag, and it forbids whitespace / slashes / any shell or
    // path character. A value that does not match is rejected exactly like an invalid npm package (see BuildAction).
    private static readonly Regex WingetId = new(
        @"^[A-Za-z0-9][A-Za-z0-9.+_-]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static PlannedAction? BuildAction(InstallEntry entry) => entry.Method switch
    {
        InstallMethod.Winget when !string.IsNullOrWhiteSpace(entry.WingetId) => WingetInstall(entry),
        InstallMethod.Npm when !string.IsNullOrWhiteSpace(entry.NpmPackage) => NpmInstall(entry),
        InstallMethod.ConfigRestore when !string.IsNullOrWhiteSpace(entry.ConfigSource)
                                       && !string.IsNullOrWhiteSpace(entry.ConfigDestination) => ConfigRestore(entry),
        _ => null,
    };

    private static CommandAction? WingetInstall(InstallEntry entry)
    {
        // Reject anything that is not a plain winget id (a leading '-' / whitespace / slash could smuggle an
        // extra flag into the --id position). Same handling as an invalid npm package: return null → reported
        // as InstallSkipReason.Incomplete by BuildPlan, never planned.
        string id = entry.WingetId!.Trim();
        if (!WingetId.IsMatch(id))
            return null;

        return new CommandAction
        {
            FileName = WingetPath,
            Arguments = new[]
            {
                "install", "--id", id, "-e", "--silent",
                "--accept-source-agreements", "--accept-package-agreements",
            },
            RequiresElevation = entry.RequiresAdmin,
            Description = $"Install {id} (winget)",
            Reason = string.IsNullOrWhiteSpace(entry.Description)
                ? "Reinstall via winget" : entry.Description,
            Risk = RiskLevel.Medium,
            Undo = UndoCapability.None,
        };
    }

    private static CommandAction? NpmInstall(InstallEntry entry)
    {
        // Reject anything that is not a plain registry package name (URL/git/tarball/path = code execution).
        if (!NpmPackageName.IsMatch(entry.NpmPackage!.Trim()))
            return null;

        return new CommandAction
        {
            FileName = ResolveNpm(),
            Arguments = new[] { "install", "-g", "--ignore-scripts", entry.NpmPackage!.Trim() },
            RequiresElevation = entry.RequiresAdmin,
            Description = $"Install {entry.NpmPackage} (npm global)",
            Reason = string.IsNullOrWhiteSpace(entry.Description)
                ? "Reinstall global npm package" : entry.Description,
            Risk = RiskLevel.Medium,
            Undo = UndoCapability.None,
        };
    }

    private static string ResolveNpm()
    {
        string programFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd");
        if (File.Exists(programFiles))
            return programFiles;
        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "npm.cmd");
        if (File.Exists(appData))
            return appData;
        return programFiles; // absolute best-effort; the gate requires rooted and the run fails cleanly if absent
    }

    private static RestoreMergeAction ConfigRestore(InstallEntry entry) => new()
    {
        Source = entry.ConfigSource!,
        Destination = entry.ConfigDestination!,
        CreateBak = true,
        Description = $"Restore config to {entry.ConfigDestination}",
        Reason = string.IsNullOrWhiteSpace(entry.Description)
            ? "Restore a configuration file (existing file kept as .bak)" : entry.Description,
        Risk = RiskLevel.Medium,
        Undo = UndoCapability.Partial,
    };
}

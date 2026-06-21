using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Planning;

/// <summary>
/// The base of the typed action model. Every destructive operation in Windows Care Kit is one of
/// these records — never a free-form command string. The <see cref="IExecutor"/> only accepts these
/// validated types, and the <c>SafetyGate</c> evaluates each one before it runs (spec §3).
/// </summary>
public abstract record PlannedAction
{
    /// <summary>Stable-per-instance id, surfaced in the <c>ExecutionLog</c>. Excluded from the plan hash.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Human-readable description shown in the dry-run preview.</summary>
    public required string Description { get; init; }

    /// <summary>Why this action is needed / why it is considered safe (spec §3 "neden silinebilir").</summary>
    public required string Reason { get; init; }

    public RiskLevel Risk { get; init; } = RiskLevel.Medium;

    public UndoCapability Undo { get; init; } = UndoCapability.None;

    /// <summary>
    /// Marks an action as PROTECTIVE (it creates rollback state — e.g. a System Restore point — rather than
    /// mutating a protected resource). Such an action is EXEMPT from driving the irreversible confirm tier:
    /// it must never escalate the gate on its own, because the user chose MORE safety, not less
    /// (UI decision §5 / critic-fix #1). The irreversible tier still arises naturally from the destructive
    /// neighbor it is staged alongside (e.g. the official uninstaller's <see cref="UndoCapability.None"/>).
    ///
    /// This is TYPE-BOUND, not a settable flag: it is a computed virtual that DEFAULTS false and is overridden
    /// to true ONLY by <see cref="CreateRestorePointAction"/>. A settable flag was a confirmation-gate bypass —
    /// any destructive action (a registry/command delete with <see cref="UndoCapability.None"/>) could set it
    /// true and exempt itself from the irreversible type-to-confirm tier (PR-5 audit FIX 1, all 4 auditors).
    /// Because the exemption is now closed to a single Core type, no destructive action can self-exempt.
    ///
    /// It is execution/tier metadata, NOT part of WHAT the action targets, so — like <see cref="Risk"/> /
    /// <see cref="Undo"/> / <see cref="FileDeleteAction.BestEffort"/> — it is intentionally excluded from
    /// <see cref="TargetSignature"/> and the plan hash. Crucially, it is NOT achieved by relabeling
    /// <see cref="Undo"/> (relabeling Undo to Full would change tier resolution elsewhere — UI decision §5 /
    /// critic MED#3).
    /// </summary>
    public virtual bool IsProtective => false;

    /// <summary>Short machine kind, e.g. <c>file.delete</c>. Used for the plan hash and logging.</summary>
    public abstract string Kind { get; }

    /// <summary>
    /// Deterministic signature of *what* this action targets (no random id, no timestamp).
    /// Two actions that touch the same target produce the same signature → stable plan hashes
    /// and reliable TOCTOU re-validation (spec §3).
    /// </summary>
    public abstract string TargetSignature();
}

/// <summary>Delete a file or directory (to the recycle bin by default).</summary>
public sealed record FileDeleteAction : PlannedAction
{
    public required string Path { get; init; }
    public bool ToRecycleBin { get; init; } = true;

    /// <summary>
    /// When true, a failure of THIS delete is recorded but does not abort the rest of the plan (§A.4):
    /// it is set ONLY by the junk-sweep scanner, where one locked temp file must not stop the cleanup.
    /// Every other delete (e.g. an uninstall leftover folder) leaves this false so its failure fails closed.
    /// This is an execution-policy flag, not part of the target — it is intentionally excluded from the
    /// signature/plan hash, just like <see cref="PlannedAction.Risk"/> and <see cref="PlannedAction.Undo"/>.
    /// </summary>
    public bool BestEffort { get; init; }

    public override string Kind => "file.delete";
    public override string TargetSignature()
        => $"{Kind}|{Path.ToLowerInvariant()}|recycle={ToRecycleBin}";
}

/// <summary>Delete a registry key, or a single value when <see cref="ValueName"/> is set.</summary>
public sealed record RegistryDeleteAction : PlannedAction
{
    public required RegistryHive Hive { get; init; }
    public required string SubKeyPath { get; init; }
    /// <summary>When null, the whole key is removed; otherwise just this value.</summary>
    public string? ValueName { get; init; }
    public RegistryView View { get; init; } = RegistryView.Registry64;
    /// <summary>Path of the <c>.reg</c> export taken before deletion, for rollback (spec §3).</summary>
    public string? BackupRegFile { get; init; }
    public override string Kind => "registry.delete";
    public override string TargetSignature()
        => $"{Kind}|{Hive}|{View}|{SubKeyPath.ToLowerInvariant()}|val={ValueName?.ToLowerInvariant() ?? "(key)"}"
         + $"|bak={BackupRegFile?.ToLowerInvariant() ?? string.Empty}";
}

/// <summary>Stop / disable / delete a Windows service.</summary>
public sealed record ServiceDeleteAction : PlannedAction
{
    public required string ServiceName { get; init; }
    public ServiceOperation Operation { get; init; } = ServiceOperation.Stop;

    /// <summary>
    /// The service's resolved executable path (from its registry <c>ImagePath</c>), when the probe could
    /// correlate one. This is the attribution EVIDENCE the leftover classifier needs: a service is only
    /// <c>ProgramOwned</c> when this path resolves UNDER the app's install directory. Null when the probe
    /// could not resolve it — in which case the classifier treats the service as Shared (fail-safe). It is
    /// correlation evidence, not part of WHAT is operated on, so it is intentionally excluded from the
    /// target signature / plan hash (like <see cref="PlannedAction.Risk"/> / <see cref="PlannedAction.Undo"/>).
    /// </summary>
    public string? ImagePath { get; init; }

    public override string Kind => "service.delete";
    public override string TargetSignature()
        => $"{Kind}|{Operation}|{ServiceName.ToLowerInvariant()}";
}

/// <summary>Disable / delete a scheduled task (by its full path, e.g. <c>\Vendor\Updater</c>).</summary>
public sealed record TaskDeleteAction : PlannedAction
{
    public required string TaskPath { get; init; }
    public TaskOperation Operation { get; init; } = TaskOperation.Disable;
    public override string Kind => "task.delete";
    public override string TargetSignature()
        => $"{Kind}|{Operation}|{TaskPath.ToLowerInvariant()}";
}

/// <summary>
/// The command-policy provenance of a <see cref="CommandAction"/> (Command-policy Phase 2). It tells the
/// <c>SafetyGate</c> which producer built the action so the gate can apply the right ADDITIVE restriction.
/// The default is <see cref="Generic"/> — fail-safe: an action that does not opt into a stricter profile gets
/// the unchanged Phase-1 treatment, and a tag can only ever FURTHER restrict, never re-admit a denied command.
/// </summary>
public enum CommandPolicyProfile
{
    /// <summary>No profile — unchanged Phase-1 command policy (the safe default for every producer).</summary>
    Generic = 0,

    /// <summary>The vendor uninstaller from <c>OfficialUninstallerPlanner</c>: when elevated, the executable must
    /// be anchored under the app's own install directory (or the narrow NSIS / System32-msiexec carve-outs).</summary>
    OfficialUninstaller,

    /// <summary>A winget install built fresh by <c>InstallPlanner</c>: the executable must be <c>winget.exe</c>.</summary>
    WingetInstall,

    /// <summary>An npm global install built fresh by <c>InstallPlanner</c>: the executable must be <c>npm.cmd</c>
    /// (the ONLY place a <c>.cmd</c> resolver is profile-admitted).</summary>
    NpmInstall,
}

/// <summary>
/// Run an executable with a structured argument list — never a shell string. This is how official
/// uninstallers / msiexec / winget are invoked; cmd/powershell string execution is blocked by the
/// gate (spec §1.1, §4).
/// </summary>
public sealed record CommandAction : PlannedAction
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public bool RequiresElevation { get; init; }
    public override string Kind => "command";

    /// <summary>
    /// The command-policy provenance set by the PRODUCER (Command-policy Phase 2). Defaults to
    /// <see cref="CommandPolicyProfile.Generic"/> (fail-safe). It is execution/policy metadata, NOT part of WHAT
    /// the action targets, so — like <see cref="PlannedAction.Risk"/> / <see cref="PlannedAction.Undo"/> — it is
    /// intentionally excluded from <see cref="TargetSignature"/> and the plan hash. The gate uses it only to apply
    /// a FURTHER restriction; it can never re-admit a command the Phase-1 checks denied.
    /// </summary>
    public CommandPolicyProfile Profile { get; init; } = CommandPolicyProfile.Generic;

    /// <summary>
    /// For <see cref="CommandPolicyProfile.OfficialUninstaller"/>, the canonical install directory the executable
    /// must sit under when the action <see cref="RequiresElevation"/> (Command-policy Phase 2 anchor). Null = no
    /// anchor available — an elevated official uninstaller with no anchor is refused by the gate (→ manual
    /// fallback). It is policy metadata, excluded from <see cref="TargetSignature"/> like <see cref="Profile"/>.
    /// </summary>
    public string? AllowedExecutableRoot { get; init; }

    /// <summary>
    /// Binds the elevation flag and a hash of the arguments (not the raw args) so the elevation decision is
    /// part of the approved hash / audit, and secrets in arguments never reach the log verbatim (spec §3, §9).
    /// </summary>
    public override string TargetSignature()
        => $"{Kind}|{FileName.ToLowerInvariant()}|elev={RequiresElevation}|argc={Arguments.Count}|{ArgHash()}";

    private string ArgHash()
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("", Arguments)));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }
}

/// <summary>Copy a file/tree (used by Backup; never copies downloadable binaries — spec §1.3).</summary>
public sealed record CopyAction : PlannedAction
{
    public required string Source { get; init; }
    public required string Destination { get; init; }

    /// <summary>Leaf names (file or folder) to skip during a tree copy — secret stores, caches, etc.
    /// The copy engine also enforces a hardened built-in superset on top of this (spec §1.3).</summary>
    public IReadOnlyList<string> ExcludeLeaves { get; init; } = Array.Empty<string>();

    /// <summary>Absolute source paths that must never be copied (full-path forbidden list).</summary>
    public IReadOnlyList<string> ForbiddenSources { get; init; } = Array.Empty<string>();

    /// <summary>When non-empty, ONLY paths matching one of these globs (relative to <see cref="Source"/>) are
    /// copied — an allow-list. Glob forms: <c>name</c>, <c>name/**</c>, <c>*.ext</c>, <c>**</c> (spec §1.3).</summary>
    public IReadOnlyList<string> Include { get; init; } = Array.Empty<string>();

    public override string Kind => "copy";
    public override string TargetSignature()
        => $"{Kind}|{Source.ToLowerInvariant()}=>{Destination.ToLowerInvariant()}"
         + $"|incl={string.Join(",", Include.Select(i => i.ToLowerInvariant()).OrderBy(i => i, StringComparer.Ordinal))}"
         + $"|excl={string.Join(",", ExcludeLeaves.Select(e => e.ToLowerInvariant()).OrderBy(e => e, StringComparer.Ordinal))}"
         + $"|forb={string.Join(",", ForbiddenSources.Select(f => f.ToLowerInvariant()).OrderBy(f => f, StringComparer.Ordinal))}";
}

/// <summary>
/// Restore a config file by merging onto the destination with a timestamped <c>.bak</c> — never a
/// blind overwrite (spec §1.4, §3).
/// </summary>
public sealed record RestoreMergeAction : PlannedAction
{
    public required string Source { get; init; }
    public required string Destination { get; init; }
    public bool CreateBak { get; init; } = true;
    public override string Kind => "restore.merge";
    public override string TargetSignature()
        => $"{Kind}|{Source.ToLowerInvariant()}=>{Destination.ToLowerInvariant()}|bak={CreateBak}";
}

/// <summary>
/// Create a Windows System Restore point before a destructive operation runs (UI decision §5). This is a
/// PROTECTIVE action: it ADDS rollback state, it does not mutate a protected resource. It is therefore
/// <see cref="PlannedAction.IsProtective"/> = true so it never escalates the confirm tier on its own
/// (critic-fix #1), and its risk is <see cref="RiskLevel.Info"/>.
///
/// It is NEVER staged as a standalone plan — it is always PREPENDED as a neighbor of the destructive action
/// it protects (the official uninstaller / a registry delete), so the irreversible tier still arises from
/// that destructive neighbor, not from the restore point (UI decision §5). Creating it is a pure system call;
/// the <c>SafetyGate</c> Allow-arm reflects that (it touches no protected resource).
/// </summary>
public sealed record CreateRestorePointAction : PlannedAction
{
    /// <summary>The description shown in the Windows System Restore UI (e.g. "Windows Care Kit — before uninstall").</summary>
    public required string RestorePointName { get; init; }

    /// <summary>
    /// The ONE protective action in the suite — type-bound true so it is tier-exempt (UI decision §5). No other
    /// action type can claim this: the base getter is false and only this override flips it (PR-5 audit FIX 1).
    /// </summary>
    public override bool IsProtective => true;

    public override string Kind => "restore.create";

    /// <summary>
    /// Only the restore-point label identifies what is created. The protective flag / risk / undo are tier
    /// metadata and are excluded from the signature (same rule as every other action's metadata).
    /// </summary>
    public override string TargetSignature()
        => $"{Kind}|{RestorePointName.ToLowerInvariant()}";
}

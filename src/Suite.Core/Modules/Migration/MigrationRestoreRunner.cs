using System.IO;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>Why a restore target did not become an executable <see cref="RestoreMergeAction"/> (honest skip list).</summary>
public enum RestoreSkipReason
{
    /// <summary>F4: machine-locked / partial portability — BLOCKED before planning, never written.</summary>
    MachineLocked,
    /// <summary>Legacy Slice-2 reason retained for old UI/report compatibility; M4 uses <see cref="InventoryOnly"/>.</summary>
    NotAllowListed,
    /// <summary>F0/F1: restoreTier says inventory/manual only — list it honestly, never write it.</summary>
    InventoryOnly,
    /// <summary>F0/F4: non-profile roots are inventory/manual only and cannot self-promote to writes.</summary>
    NonProfileRoot,
    /// <summary>Slice 2 executes only single-file ConfigWrite-class strategies; others are deferred.</summary>
    UnsupportedStrategy,
    /// <summary>The packaged source bytes are missing from the package.</summary>
    SourceMissing,
    /// <summary>Typed rebind rejected the destination (absolute/traversal/unknown-token/target-escape).</summary>
    RebindRejected,
    /// <summary>The SafetyGate refused the destination (outside the current/target profile, protected tree).</summary>
    GateBlocked,
    /// <summary>Already completed in the checkpoint (<c>.kurulum_state.json</c>) — resume skips it.</summary>
    AlreadyDone,
}

/// <summary>One restore target that did not enter the plan, with the reason and a human note.</summary>
public sealed record RestoreSkip(MigrationRestoreTarget Target, RestoreSkipReason Reason, string Note);

/// <summary>
/// The output of building a restore plan: the gate-approved <see cref="OperationPlan"/> of
/// <see cref="RestoreMergeAction"/>s, the targets that were skipped (with reasons), and the action-id →
/// manifest-entry-id correlation used by the checkpoint to mark the right entry done/failed (mirrors
/// <see cref="InstallPlanResult.ActionEntryIds"/>).
/// </summary>
public sealed record MigrationRestorePlanResult(
    OperationPlan Plan,
    IReadOnlyList<RestoreSkip> Skipped,
    IReadOnlyDictionary<string, string> ActionEntryIds)
{
    /// <summary>Restore action id → target, for pure report/success-screen classification.</summary>
    public IReadOnlyDictionary<string, MigrationRestoreTarget> RestoreActionTargets { get; init; }
        = new Dictionary<string, MigrationRestoreTarget>(StringComparer.Ordinal);

    /// <summary>Reinstall action id → install entry, for pure report/success-screen classification.</summary>
    public IReadOnlyDictionary<string, InstallEntry> InstallActionEntries { get; init; }
        = new Dictionary<string, InstallEntry>(StringComparer.Ordinal);

    /// <summary>Install planner skips when a package install manifest was supplied.</summary>
    public IReadOnlyList<InstallSkip> InstallSkipped { get; init; } = Array.Empty<InstallSkip>();

    /// <summary>Manual-after install checklist entries when a package install manifest was supplied.</summary>
    public IReadOnlyList<InstallEntry> InstallManualChecklist { get; init; } = Array.Empty<InstallEntry>();
}

/// <summary>
/// The migration RESTORE planner (decision §B). It reads a package's <c>migration-manifest.json</c> and turns
/// each <see cref="MigrationRestoreTarget"/> into a gate-approved dry-run <see cref="RestoreMergeAction"/> that
/// the existing <c>GatedExecutor</c> → <c>CopyAdapter.Merge</c> (atomic, .bak-backed) executes. It is read-only:
/// it emits a plan, it never writes anything itself (execution is the sanctioned executor).
///
/// <para><b>F4 (the load-bearing fail-safe, wired into the execution path):</b> a machine-locked / partial
/// target NEVER becomes a <see cref="RestoreMergeAction"/> — the runner makes its OWN block decision here
/// (the InstallPlanner skip pattern); it does NOT consult <c>PortabilityBadge.MayClaimWorks</c>, which is
/// presentation-only. Because the executor can only run actions that are IN the plan, a blocked target is
/// physically unable to write. Proven by the "0 RestoreMergeAction + 0 writes" test.</para>
///
/// <para><b>F2:</b> only allow-listed, inner-path-clean recipes + single-file ConfigWrite-class strategies are
/// planned; the runner never edits restored file bytes (inner-path/SID rebind is Slice 3).</para>
///
/// <para><b>Typed rebind:</b> the destination is recomputed on the TARGET machine from the closed
/// <see cref="KnownFolder"/> + normalized relative path via <see cref="RecipePathResolver"/> — never a blind
/// string-replace. Absolute / traversal / unknown-token / profile-escape destinations are rejected BEFORE the
/// gate. The gate then independently confirms the destination is inside the current/target profile.</para>
/// </summary>
public sealed class MigrationRestoreRunner
{
    private readonly RecipePathResolver _paths;
    private readonly ISafetyGate _gate;

    public MigrationRestoreRunner(RecipePathResolver targetPaths, ISafetyGate gate)
    {
        _paths = targetPaths ?? throw new ArgumentNullException(nameof(targetPaths));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <summary>
    /// Build the restore plan for <paramref name="manifest"/> whose payload bytes live under
    /// <paramref name="packageDirectory"/>, skipping targets already <see cref="RestoreEntryStatus.Done"/> in
    /// <paramref name="state"/> (resume). The plan's actions are gate-approved; the skip list is the honest
    /// "what will NOT be restored, and why".
    /// </summary>
    public MigrationRestorePlanResult BuildPlan(
        MigrationRestoreManifest manifest,
        string packageDirectory,
        RestoreState state,
        DateTime utc,
        InstallManifest? installManifest = null,
        InstallPlanner? installPlanner = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentNullException.ThrowIfNull(state);

        var actions = new List<PlannedAction>();
        var skipped = new List<RestoreSkip>();
        var actionEntryIds = new Dictionary<string, string>();
        var restoreActionTargets = new Dictionary<string, MigrationRestoreTarget>(StringComparer.Ordinal);
        var installActionEntries = new Dictionary<string, InstallEntry>(StringComparer.Ordinal);
        IReadOnlyList<InstallSkip> installSkipped = Array.Empty<InstallSkip>();
        IReadOnlyList<InstallEntry> installManualChecklist = Array.Empty<InstallEntry>();

        if (installManifest is not null)
        {
            if (installPlanner is null)
                throw new ArgumentNullException(nameof(installPlanner), "An InstallPlanner is required when an install manifest is supplied.");

            InstallPlanResult installPlan = installPlanner.BuildPlan(installManifest, state, utc);
            installSkipped = installPlan.Skipped;
            installManualChecklist = installPlan.ManualChecklist;
            foreach (PlannedAction installAction in installPlan.Plan.Actions)
            {
                actions.Add(installAction);
                if (installPlan.ActionEntryIds.TryGetValue(installAction.Id, out string? installEntryId))
                {
                    actionEntryIds[installAction.Id] = installEntryId;
                    InstallEntry? entry = installManifest.Entries
                        .FirstOrDefault(e => string.Equals(e.Id, installEntryId, StringComparison.OrdinalIgnoreCase));
                    if (entry is not null)
                        installActionEntries[installAction.Id] = entry;
                }
            }
        }

        foreach (MigrationRestoreTarget target in RestoreRunbook.OrderTargets(
                     manifest.Targets ?? Array.Empty<MigrationRestoreTarget>()))
        {
            // 0) Resume: a completed entry is skipped without re-planning.
            if (state.IsDone(target.EntryId))
            {
                skipped.Add(new RestoreSkip(target, RestoreSkipReason.AlreadyDone, "Already restored (checkpoint)"));
                continue;
            }

            // 1) F4 fail-safe BLOCK — wired into the execution path: machine-locked / partial → NO action.
            //    The runner decides this itself; it never trusts a presentation badge.
            if (target.PortabilityClass != PortabilityClass.ProfileRelative)
            {
                skipped.Add(new RestoreSkip(target, RestoreSkipReason.MachineLocked,
                    "Machine-locked / partial: not restored — re-install or re-login on the new machine"));
                continue;
            }

            // 2) F0/F4 cap: non-profile roots are inventory/manual only, even if a package was tampered to
            //    claim a higher restoreTier. The loader caps recipe data; the runner keeps the execution-path
            //    double block.
            if (!IsProfileFolder(target.KnownFolder))
            {
                skipped.Add(new RestoreSkip(target, RestoreSkipReason.NonProfileRoot,
                    "EN: Non-profile data is listed for manual restore/re-add only; no file write is planned. TR: Profil disi veri yalnizca elle geri ekleme/listedir; dosya yazma planlanmaz."));
                continue;
            }

            RestoreTier effectiveTier = EffectiveRestoreTier(target);

            // 3) F0 restoreTier gate: catalog data, not a hardcoded recipe-id allow-list, decides config-copy
            //    eligibility for new manifests. Legacy manifests with no tier fall back to the old tiny set.
            if (effectiveTier < RestoreTier.ConfigCopy)
            {
                RestoreSkipReason reason = target.RestoreTier == RestoreTier.Unspecified
                    ? RestoreSkipReason.NotAllowListed
                    : RestoreSkipReason.InventoryOnly;
                skipped.Add(new RestoreSkip(target, reason,
                    reason == RestoreSkipReason.NotAllowListed
                        ? "EN: Legacy package target is not in the old inner-path-clean restore set; list it manually. TR: Eski paket hedefi eski ic-yol-temiz geri yukleme listesinde degil; elle listeleyin."
                        : "EN: Inventory/manual-only item; it was backed up or detected but cannot be safely restored automatically. TR: Envanter/manuel kalem; yedeklendi veya tespit edildi ama guvenle otomatik geri yuklenemez."));
                continue;
            }

            // 4) M4 executes ConfigWrite/MergeAfterInstall-class single-file strategies only.
            if (target.RestoreStrategy is not (RestoreStrategy.ConfigWrite or RestoreStrategy.MergeAfterInstall))
            {
                skipped.Add(new RestoreSkip(target, RestoreSkipReason.UnsupportedStrategy,
                    $"Strategy {target.RestoreStrategy} is not executed by the M4 file-placement runner"));
                continue;
            }

            // 5) Typed rebind: recompute the destination on the TARGET machine from the closed KnownFolder +
            //    normalized relative path. Any escape/traversal/unknown-token is rejected here, before the gate.
            string destination;
            try
            {
                destination = _paths.Resolve(target.KnownFolder, target.RelativePath);
            }
            catch (RecipePathException ex)
            {
                skipped.Add(new RestoreSkip(target, RestoreSkipReason.RebindRejected, ex.Message));
                continue;
            }

            // 6) The packaged source bytes must exist in the package.
            string source = Path.GetFullPath(Path.Combine(
                packageDirectory, target.PackageRelativeSource.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(source))
            {
                skipped.Add(new RestoreSkip(target, RestoreSkipReason.SourceMissing,
                    $"Packaged source missing: {target.PackageRelativeSource}"));
                continue;
            }

            // 7) Build the typed action (.bak-backed, atomic merge at execution time).
            var action = new RestoreMergeAction
            {
                Source = source,
                Destination = destination,
                CreateBak = true,
                Description = $"Restore {target.RecipeId} → {target.KnownFolder}/{target.RelativePath}",
                Reason = "Restore a profile-relative config file (existing file kept as .bak)",
                Risk = RiskLevel.Medium,
                Undo = UndoCapability.Partial,
            };

            // 8) Gate every action — a blocked one is reported, never planned (the gate independently confirms
            //    the destination is inside the current/target profile and not a protected tree).
            SafetyVerdict verdict = _gate.Evaluate(action);
            if (!verdict.Allowed)
            {
                skipped.Add(new RestoreSkip(target, RestoreSkipReason.GateBlocked, verdict.Reason));
                continue;
            }

            actions.Add(action);
            actionEntryIds[action.Id] = target.EntryId;
            restoreActionTargets[action.Id] = target;
        }

        var plan = new OperationPlan("Restore migrated settings", "migration-restore", actions, utc);
        return new MigrationRestorePlanResult(plan, skipped, actionEntryIds)
        {
            RestoreActionTargets = restoreActionTargets,
            InstallActionEntries = installActionEntries,
            InstallSkipped = installSkipped,
            InstallManualChecklist = installManualChecklist,
        };
    }

    private static bool IsProfileFolder(KnownFolder folder)
        => folder is KnownFolder.UserProfile or KnownFolder.AppData or KnownFolder.LocalAppData;

    private static RestoreTier EffectiveRestoreTier(MigrationRestoreTarget target)
        => target.RestoreTier == RestoreTier.Unspecified
            ? RestoreAllowList.LegacyTierFor(target.RecipeId)
            : target.RestoreTier;
}

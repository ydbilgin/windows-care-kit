using System.IO;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// Everything the runner needs to finalize ONE <see cref="MigrationRestoreTarget"/> after the copy of a single
/// file lands in the package (decision §"New types"). It is private provenance carried by action id — the
/// <see cref="ItemRecipePath"/> is the declared, normalized recipe-relative path that travels with the
/// sandbox-passing item (never re-indexed from <c>recipe.Items</c>), which is what makes the index bug
/// structurally impossible.
/// </summary>
/// <param name="Recipe">The recipe this item came from.</param>
/// <param name="Meta">The side-carrier portability/restore meta for the matching entry.</param>
/// <param name="KnownFolder">The closed-enum root the destination anchors to (recipe's detect KnownFolder).</param>
/// <param name="ItemRecipePath">The item's declared path relative to <see cref="KnownFolder"/> (forward slashes).</param>
/// <param name="PackageRelativeSource">Where the bytes live inside the package (<c>Entry.Target</c>, forward slashes).</param>
/// <param name="Destination">The absolute destination path the copy wrote to (inside the package root).</param>
internal sealed record ProvisionalRestoreTarget(
    MigrationRecipe Recipe,
    MigrationItemMeta Meta,
    KnownFolder KnownFolder,
    string ItemRecipePath,
    string PackageRelativeSource,
    string Destination);

/// <summary>
/// The plan side of the migration backup: the gate-bound <see cref="OperationPlan"/> of copy actions (one per
/// backed-up item) and the honest skip list (sandbox skips PLUS grammar/containment/duplicate skips surfaced
/// here, never silently dropped). The action→provisional side-map is carried INTERNALLY (the
/// <see cref="ProvisionalRestoreTarget"/> is runner-private provenance the caller treats opaquely): the caller
/// hashes <see cref="Plan"/>, then passes this whole result straight back to <see cref="MigrationBackupRunner.Run"/>.
/// </summary>
/// <param name="Plan">The copy actions, in recipe/item order. Hashed + authorized as a WHOLE before <c>Run</c>.</param>
/// <param name="SkippedItems">Sandbox skips + grammar/containment/duplicate skips, surfaced.</param>
public sealed record MigrationBackupPlanResult(
    OperationPlan Plan,
    IReadOnlyList<RecipeItemSkip> SkippedItems)
{
    /// <summary>action.Id → provisional restore target, only for single-file sources. Runner-internal provenance.</summary>
    internal IReadOnlyDictionary<string, ProvisionalRestoreTarget> ByActionId { get; init; } =
        new Dictionary<string, ProvisionalRestoreTarget>(StringComparer.Ordinal);
}

/// <summary>
/// The run side of the migration backup: whether the plan was authorized, the copied/skipped report, the
/// restore manifest of what ACTUALLY landed (succeeded single files only), the plan-time skips, and the
/// finalization skips (hash / shape / unsafe-path / manifest-not-written failures) surfaced honestly.
/// </summary>
/// <param name="Authorized">False when the whole plan was refused — then NOTHING was written.</param>
/// <param name="CopyReport">The reused Backup copied/skipped shaping.</param>
/// <param name="Manifest">The restore manifest that was saved (empty when authorized-but-nothing-landed or gate-blocked).</param>
/// <param name="SkippedItems">The plan-time skips (carried through from <see cref="MigrationBackupPlanResult"/>).</param>
/// <param name="FinalizationSkips">Post-copy failures: missing/shape-changed destination, hash failure, unsafe path, or manifest-not-written.</param>
public sealed record MigrationBackupRunResult(
    bool Authorized,
    CopySkipReport CopyReport,
    MigrationRestoreManifest Manifest,
    IReadOnlyList<RecipeItemSkip> SkippedItems,
    IReadOnlyList<RecipeItemSkip> FinalizationSkips);

/// <summary>
/// The PRODUCTION migration BACKUP orchestrator (decision §FINAL DESIGN). It resolves each recipe through the
/// real sandbox, bridges the passing items to <c>BackupEntry</c> values, and projects them into copy actions
/// whose destinations are inside the chosen package directory. The copy runs through the gated execution seam
/// (<see cref="IBackupExecutor"/>, NOT a direct CopyAdapter — Core cannot reference Suite.Execution), and the
/// restore manifest is emitted POST-execution from what the sanctioned executor actually copied, so a guard
/// here is always wired to the real write path, never just unit-tested in isolation.
///
/// <para><b>Security invariants (decision §cx findings + critic fixes):</b></para>
/// <list type="bullet">
/// <item>The recipe <c>id</c> grammar is enforced at LOAD (<see cref="MigrationRecipeLoader"/>); here every copy
/// destination is additionally proven contained in the normalized package root via
/// <see cref="RecipePathResolver.IsContained"/> (case-insensitive, separator-safe) — belt and suspenders.</item>
/// <item>Duplicate recipe ids / <c>Entry.Id</c> / normalized <c>Entry.Target</c> across the supplied set are
/// rejected (skip + surface) so two recipes can never silently collide on one package subdir.</item>
/// <item>The restore-manifest SHA is computed POST-exec from the DESTINATION (Done + <c>FileExists</c>); a hash
/// or shape failure is surfaced as a finalization-skip, never silently treated as restored.</item>
/// <item>Before the manifest write, the package ROOT is re-gated (TOCTOU); on block it SURFACES (does not throw)
/// — a backup whose copies already succeeded must not crash on a racing reparse swap (critic fix #7).</item>
/// </list>
///
/// <para><b>F4 is RESTORE-ONLY:</b> the backup runner packages machine-locked / partial items too (refusing to
/// back them up = permanent data loss); the portability class rides on every restore target so the RESTORE
/// runner — and only it — blocks placing them. There is NO portability block here.</para>
/// </summary>
public sealed class MigrationBackupRunner
{
    private readonly RecipeResolver _resolver;
    private readonly IBackupExecutor _executor;
    private readonly IHasher _hasher;
    private readonly IFileSystem _fs;
    private readonly MigrationRestoreManifestStore _store;
    private readonly ISafetyGate _gate;

    public MigrationBackupRunner(
        RecipeResolver resolver,
        IBackupExecutor executor,
        IHasher hasher,
        IFileSystem fs,
        MigrationRestoreManifestStore store,
        ISafetyGate gate)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <summary>
    /// Resolve + bridge every recipe and project the passing items into copy actions whose destinations are
    /// inside <paramref name="packageDir"/>. No copy and no hash happen here — this is the dry-run the caller
    /// presents (UI seam #2) before hashing the plan and calling <see cref="Run"/>.
    /// </summary>
    public MigrationBackupPlanResult BuildPlan(IEnumerable<MigrationRecipe> recipes, string packageDir, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDir);

        string packageRoot = Path.GetFullPath(packageDir);
        char sep = Path.DirectorySeparatorChar;

        var actions = new List<PlannedAction>();
        var byActionId = new Dictionary<string, ProvisionalRestoreTarget>(StringComparer.Ordinal);
        var skipped = new List<RecipeItemSkip>();

        // Duplicate-rejection state across the WHOLE supplied set (decision §"Duplicate detection").
        var seenRecipeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEntryIds = new HashSet<string>(StringComparer.Ordinal);
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (MigrationRecipe recipe in recipes)
        {
            // A duplicate recipe id in the set would re-derive the same Entry.Target subdir and silently
            // overwrite the earlier copy — reject the whole recipe (skip + surface), never overwrite.
            if (!seenRecipeIds.Add(recipe.Id))
            {
                skipped.Add(new RecipeItemSkip($"recipe:{recipe.Id}", "duplicate recipe id in the supplied set"));
                continue;
            }

            ResolvedRecipe resolved = _resolver.Resolve(recipe);
            foreach (RecipeItemSkip s in resolved.Skipped)
                skipped.Add(s);

            IReadOnlyList<BridgedMigrationItem> bridged = RecipeToBackupEntry.Bridge(resolved);

            // bridged[k] and resolved.Items[k] are the SAME source list in the SAME order (the bridge maps 1:1
            // over resolved.Items), so this index is skip-proof — and we read RecipePath, never recipe.Items.
            for (int k = 0; k < bridged.Count; k++)
            {
                BackupEntry entry = bridged[k].Entry;
                MigrationItemMeta meta = bridged[k].Meta;
                string itemRecipePath = resolved.Items[k].RecipePath;

                string dest = Path.GetFullPath(Path.Combine(packageRoot, entry.Target.Replace('/', sep)));

                // Containment guard (critic fix #2): case-insensitive, separator-safe via the canonical helper —
                // a hand-rolled StartsWith would false-skip "D:\Pkg" vs "d:\pkg". Belt-and-suspenders over the
                // load-time id grammar; nothing may land outside the package root for ANY recipe id.
                if (!RecipePathResolver.IsContained(packageRoot, dest))
                {
                    skipped.Add(new RecipeItemSkip(entry.Target, "copy destination escapes the package root"));
                    continue;
                }

                // Duplicate Entry.Id / normalized Entry.Target → skip + surface (no silent overwrite/collision).
                if (!seenEntryIds.Add(entry.Id))
                {
                    skipped.Add(new RecipeItemSkip(entry.Id, "duplicate entry id in the supplied set"));
                    continue;
                }
                string normalizedTarget = entry.Target.Replace('\\', '/').Trim('/');
                if (!seenTargets.Add(normalizedTarget))
                {
                    skipped.Add(new RecipeItemSkip(entry.Target, "duplicate package target in the supplied set"));
                    continue;
                }

                var copy = new CopyAction
                {
                    Source = entry.Source,
                    Destination = dest,
                    ExcludeLeaves = entry.Exclude,
                    Include = entry.Include,
                    Description = recipe.DisplayName,
                    Reason = "migration backup",
                    Risk = RiskLevel.Low,       // a real copy is Low (Info is the empty/no-risk sentinel).
                    Undo = UndoCapability.None, // packaging a copy into a fresh package dir has no undo.
                };
                actions.Add(copy);

                // A restore target only makes sense for a single FILE (Slice 2 restore is single-file only).
                // A directory item is still backed up (copy action) but gets no provisional/restore target.
                if (_fs.FileExists(entry.Source))
                {
                    byActionId[copy.Id] = new ProvisionalRestoreTarget(
                        recipe, meta, recipe.Detect.KnownFolder, itemRecipePath, entry.Target, dest);
                }
            }
        }

        var plan = new OperationPlan("Migration backup", "migration-backup", actions, utc);
        return new MigrationBackupPlanResult(plan, skipped) { ByActionId = byActionId };
    }

    /// <summary>
    /// Execute <paramref name="plan"/> through the gated executor, then finalize the restore manifest from what
    /// actually landed and save it at the package root. Authorization is whole-plan all-or-nothing: ONE
    /// gate-blocked destination ⇒ the WHOLE backup is refused (<c>Authorized == false</c>, nothing written).
    /// </summary>
    public MigrationBackupRunResult Run(MigrationBackupPlanResult plan, string approvedPlanHash, string packageDir)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDir);

        BackupExecutionReport report = _executor.Execute(plan.Plan, approvedPlanHash);
        CopySkipReport copyReport = BackupRunner.BuildCopyReport(plan.Plan, report);

        // Refused run: mirror BackupRunner — write NOTHING (no manifest), empty result.
        if (!report.Authorized)
        {
            return new MigrationBackupRunResult(
                Authorized: false,
                CopyReport: copyReport,
                Manifest: new MigrationRestoreManifest(MigrationRestoreManifest.CurrentSchemaVersion, Array.Empty<MigrationRestoreTarget>()),
                SkippedItems: plan.SkippedItems,
                FinalizationSkips: Array.Empty<RecipeItemSkip>());
        }

        var targets = new List<MigrationRestoreTarget>();
        var finalizationSkips = new List<RecipeItemSkip>();

        foreach (CopyFileOutcome oc in copyReport.Copied)
        {
            // No provisional ⇒ this copy was a directory item (no single-file restore target) — not an error.
            if (!plan.ByActionId.TryGetValue(oc.EntryId, out ProvisionalRestoreTarget? prov))
                continue;

            // The executor reported Done, but the destination must still EXIST AS A FILE before we trust it
            // (a Done copy whose destination is a directory or vanished is a shape change → finalization-skip).
            if (!_fs.FileExists(oc.Destination))
            {
                finalizationSkips.Add(new RecipeItemSkip(prov.PackageRelativeSource,
                    "destination is not an existing file after copy (shape change)"));
                continue;
            }

            // Hash the PACKAGED bytes (provenance + integrity). A hash failure is surfaced, never swallowed.
            string sha;
            try
            {
                sha = _hasher.ComputeFileSha256(oc.Destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                finalizationSkips.Add(new RecipeItemSkip(prov.PackageRelativeSource, $"hash failed: {ex.Message}"));
                continue;
            }

            MigrationRestoreTarget? target = MigrationRestoreManifestBuilder.BuildTarget(
                prov.Recipe, prov.Meta, prov.KnownFolder, prov.ItemRecipePath, prov.PackageRelativeSource, sha);
            if (target is null)
            {
                // The recipe path could not be safely normalized (F5) — no target, surfaced.
                finalizationSkips.Add(new RecipeItemSkip(prov.PackageRelativeSource,
                    "restore target rejected (unsafe recipe path)"));
                continue;
            }
            targets.Add(target);
        }

        // Re-gate the package ROOT before the manifest write (TOCTOU). Intentional divergence from
        // BackupIntegrityWriter (critic fix #7): that throws on block; here a backup whose copies already
        // SUCCEEDED must not crash because the manifest write races a reparse swap — SURFACE and skip the save.
        var manifestProbe = new CopyAction
        {
            Source = packageDir,
            Destination = _store.PathFor(packageDir),
            Description = "migration manifest write",
            Reason = "re-gate the package root before writing the restore manifest",
            Risk = RiskLevel.Low,
            Undo = UndoCapability.None,
        };
        SafetyVerdict verdict = _gate.Evaluate(manifestProbe);
        if (!verdict.Allowed)
        {
            finalizationSkips.Add(new RecipeItemSkip(
                MigrationRestoreManifest.FileName, $"manifest not written (gate): {verdict.Reason}"));
            return new MigrationBackupRunResult(
                Authorized: true,
                CopyReport: copyReport,
                Manifest: new MigrationRestoreManifest(MigrationRestoreManifest.CurrentSchemaVersion, Array.Empty<MigrationRestoreTarget>()),
                SkippedItems: plan.SkippedItems,
                FinalizationSkips: finalizationSkips);
        }

        // An authorized run that copied no single file still saves a VALID empty manifest (Targets = []).
        var manifest = new MigrationRestoreManifest(MigrationRestoreManifest.CurrentSchemaVersion, targets);
        _store.Save(packageDir, manifest);

        return new MigrationBackupRunResult(
            Authorized: true,
            CopyReport: copyReport,
            Manifest: manifest,
            SkippedItems: plan.SkippedItems,
            FinalizationSkips: finalizationSkips);
    }
}

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Execution.Adapters;
using WindowsCareKit.Win32;

// ---------------------------------------------------------------------------
// MigrationE2E — end-to-end migration export/import round-trip harness.
//
// Proves the format-migration value with REAL profiles (or fabricated temp
// dirs for host-safe CI): backup Profile A → package + zip export → restore
// to Profile B → verify every manifest target landed with matching SHA.
//
// CLI:
//   MigrationE2E
//     --profileA  <root>    Profile A USERPROFILE root
//     --appdataA  <path>    Profile A APPDATA (Roaming)
//     --localA    <path>    Profile A LOCALAPPDATA
//     --profileB  <root>    Profile B USERPROFILE root
//     --appdataB  <path>    Profile B APPDATA (Roaming)
//     --localB    <path>    Profile B LOCALAPPDATA
//     --package   <dir>     Package output directory
//     --output    <dir>     Evidence report output directory
//     --gitInstall    <ok|fail>   (optional) git install exit status from run.cmd
//     --vscodeInstall <ok|fail>   (optional) VS Code install exit status from run.cmd
//
// SAFETY CONTRACT
//   - The harness NEVER resolves real KnownFolders.  All roots are explicit args.
//   - It NEVER installs anything, opens any window, or touches the real host profile.
//   - The SafetyGate is wired to treat --profileB as the current user profile
//     (only that sub-tree is a legal restore destination).
//
// PROOF SCOPE
//   Round-trip PASS proves the migration engine over real config; it does NOT
//   depend on the real installs succeeding (config is representative).
//   Real installs are best-effort environmental setup; the proof is the
//   round-trip over real config.
// ---------------------------------------------------------------------------

namespace WindowsCareKit.Tools.MigrationE2E;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // Exit codes
    private const int ExitOk = 0;
    private const int ExitVerifyFail = 1;
    private const int ExitUsageError = 2;

    // Required restore targets that must be verified for the round-trip to count.
    private static readonly string[] RequiredRestoredRecipes =
    {
        "git.config",
        "anthropic.claude-code",
    };

    private const string SyntheticInventoryRecipeId = "wck.synthetic.inventory-only";

    internal static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryParseSelfTestArgs(args, out var selfCfg, out string selfArgError))
            {
                Console.Error.WriteLine($"[E2E] ERROR: {selfArgError}");
                PrintUsage();
                return ExitUsageError;
            }

            try
            {
                IReadOnlyList<MigrationRecipe> selfRecipes = SeedSelfTest(selfCfg);
                return Run(selfCfg, selfRecipes);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[E2E] UNHANDLED EXCEPTION: {ex}");
                return ExitVerifyFail;
            }
        }

        // --- parse args ---
        if (!TryParseArgs(args, out var cfg, out string argError))
        {
            Console.Error.WriteLine($"[E2E] ERROR: {argError}");
            PrintUsage();
            return ExitUsageError;
        }

        Console.WriteLine("[E2E] ===== Migration E2E Round-Trip Harness =====");
        Console.WriteLine($"[E2E] Profile A       : {cfg.ProfileA}");
        Console.WriteLine($"[E2E] Profile B       : {cfg.ProfileB}");
        Console.WriteLine($"[E2E] Package         : {cfg.PackageDir}");
        Console.WriteLine($"[E2E] Output          : {cfg.OutputDir}");
        Console.WriteLine($"[E2E] Git install     : {cfg.GitInstall ?? "(not reported)"}");
        Console.WriteLine($"[E2E] VS Code install : {cfg.VsCodeInstall ?? "(not reported)"}");
        Console.WriteLine("[E2E] NOTE: Round-trip PASS proves the migration engine over real config;");
        Console.WriteLine("[E2E]       it does NOT depend on the real installs succeeding.");

        try
        {
            return Run(cfg);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[E2E] UNHANDLED EXCEPTION: {ex}");
            return ExitVerifyFail;
        }
    }

    private static int Run(Config cfg, IReadOnlyList<MigrationRecipe>? recipeOverride = null)
    {
        var evidence = new EvidenceReport();
        evidence.Config = new ConfigSummary(cfg.ProfileA, cfg.AppDataA, cfg.LocalA,
                                            cfg.ProfileB, cfg.AppDataB, cfg.LocalB,
                                            cfg.PackageDir, cfg.OutputDir, cfg.StateDir);
        evidence.GitInstallStatus = cfg.GitInstall ?? "not-reported";
        evidence.VsCodeInstallStatus = cfg.VsCodeInstall ?? "not-reported";

        // ------------------------------------------------------------------ 1. LOAD BUILTIN RECIPES
        Console.WriteLine("[E2E] Step 1: loading builtin recipes...");
        IReadOnlyList<MigrationRecipe> recipes = recipeOverride ?? BuiltinRecipeSource.LoadAll();
        Console.WriteLine($"[E2E]   loaded {recipes.Count} recipes: {string.Join(", ", recipes.Select(r => r.Id))}");
        evidence.RecipesLoaded = recipes.Select(r => r.Id).ToList();

        // ------------------------------------------------------------------ 2. BACKUP A → PACKAGE
        Console.WriteLine("[E2E] Step 2: backup Profile A → package...");

        var rootsA = new ProfileRoots(cfg.ProfileA, cfg.AppDataA, cfg.LocalA);

        // Gate: treat the package dir as the "current profile" so the SafetyGate
        // authorizes copy-writes into it (mirrors MigrationRestoreTestData.GateAllowingPackage).
        SafetyGate backupGate = BuildGate(
            profileRoot: cfg.PackageDir,
            usersRoot: cfg.PackageDir);

        var backupExecutor = new GatedExecutor(
            backupGate,
            new ExecutionLog(
                Path.Combine(Path.GetTempPath(), $"wck-e2e-backup-{Guid.NewGuid():N}.jsonl"),
                new LogRedactor(null, null)),
            new ThrowingFileDeleteAdapter(),
            new ThrowingRegistryAdapter(),
            new ThrowingServiceAdapter(),
            new ThrowingTaskAdapter(),
            new ThrowingProcessAdapter(),
            new CopyAdapter());

        var backupRunner = new MigrationBackupRunner(
            new RecipeResolver(new RecipePathResolver(rootsA), new Win32RecipeFileSystem()),
            new BackupExecutorAdapter(backupExecutor),
            new Sha256Hasher(),
            new PhysicalFileSystem(),
            new MigrationRestoreManifestStore(),
            backupGate);

        DateTime utc = DateTime.UtcNow;
        MigrationBackupPlanResult plan = backupRunner.BuildPlan(recipes, cfg.PackageDir, utc);

        Console.WriteLine($"[E2E]   plan actions : {plan.Plan.Actions.Count}");
        Console.WriteLine($"[E2E]   plan skips   : {plan.SkippedItems.Count}");
        foreach (var skip in plan.SkippedItems)
            Console.WriteLine($"[E2E]     SKIPPED [{skip.ItemPath}]: {skip.Reason}");

        MigrationBackupRunResult backup = backupRunner.Run(plan, plan.Plan.ComputeHash(), cfg.PackageDir);

        Console.WriteLine($"[E2E]   authorized   : {backup.Authorized}");
        Console.WriteLine($"[E2E]   targets      : {backup.Manifest.Targets.Count}");
        foreach (var t in backup.Manifest.Targets)
            Console.WriteLine($"[E2E]     TARGET [{t.RecipeId}] {t.KnownFolder}/{t.RelativePath}  sha={t.Sha256[..8]}...");
        foreach (var fs in backup.FinalizationSkips)
            Console.WriteLine($"[E2E]     FINALIZATION-SKIP [{fs.ItemPath}]: {fs.Reason}");

        evidence.Backup = new BackupSummary(
            backup.Authorized,
            backup.Manifest.Targets.Count,
            backup.SkippedItems.Select(s => new SkipEntry(s.ItemPath, s.Reason)).ToList(),
            backup.FinalizationSkips.Select(s => new SkipEntry(s.ItemPath, s.Reason)).ToList(),
            backup.Manifest.Targets.Select(t => new TargetEntry(t.RecipeId, t.KnownFolder.ToString(), t.RelativePath, t.Sha256)).ToList());

        if (!backup.Authorized)
        {
            Console.Error.WriteLine("[E2E] FAIL: backup was refused (gate blocked the package dir).");
            WriteReport(cfg.OutputDir, evidence, pass: false, "backup refused");
            return ExitVerifyFail;
        }

        // FIX 3: zero backup targets => FAIL (not a warning).
        if (backup.Manifest.Targets.Count == 0)
        {
            Console.Error.WriteLine("[E2E] FAIL: no targets backed up. At minimum git.config and anthropic.claude-code must be present in Profile A.");
            WriteReport(cfg.OutputDir, evidence, pass: false, "zero targets backed up — non-vacuous round-trip requires at least git.config and anthropic.claude-code");
            return ExitVerifyFail;
        }

        // ------------------------------------------------------------------ 3. ZIP EXPORT
        Console.WriteLine("[E2E] Step 3: zip export of package...");
        string zipPath = Path.Combine(cfg.PackageDir, "migration-export.zip");
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        // Collect all package files except an existing zip (avoid recursion).
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (string file in Directory.EnumerateFiles(cfg.PackageDir, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(file, zipPath, StringComparison.OrdinalIgnoreCase))
                    continue; // skip the zip itself
                string rel = Path.GetRelativePath(cfg.PackageDir, file);
                archive.CreateEntryFromFile(file, rel.Replace('\\', '/'));
            }
        }
        long zipBytes = new FileInfo(zipPath).Length;
        Console.WriteLine($"[E2E]   zip: {zipPath}  ({zipBytes:N0} bytes)");
        evidence.ZipPath = zipPath;
        evidence.ZipBytes = zipBytes;

        // ------------------------------------------------------------------ 4. LOAD MANIFEST
        Console.WriteLine("[E2E] Step 4: loading restore manifest from package...");
        var manifestStore = new MigrationRestoreManifestStore();
        MigrationRestoreManifest manifest = manifestStore.Load(cfg.PackageDir);
        Console.WriteLine($"[E2E]   manifest schema v{manifest.SchemaVersion}, {manifest.Targets.Count} targets");

        // ------------------------------------------------------------------ 5. RESTORE TO PROFILE B
        Console.WriteLine("[E2E] Step 5: restore to Profile B...");

        var rootsB = new ProfileRoots(cfg.ProfileB, cfg.AppDataB, cfg.LocalB);
        var pathsB = new RecipePathResolver(rootsB);
        IReadOnlyDictionary<string, FileSnapshot> preRestoreSnapshots = SnapshotTargets(manifest, pathsB);

        // Gate: usersRoot = parent of profileB, currentUserProfile = profileB.
        string usersBRoot = Path.GetDirectoryName(cfg.ProfileB)
            ?? cfg.ProfileB; // fallback: treat profileB itself as root if no parent

        SafetyGate restoreGate = BuildGate(
            profileRoot: cfg.ProfileB,
            usersRoot: usersBRoot);

        var restoreExecutor = new GatedExecutor(
            restoreGate,
            new ExecutionLog(
                Path.Combine(Path.GetTempPath(), $"wck-e2e-restore-{Guid.NewGuid():N}.jsonl"),
                new LogRedactor(null, null)),
            new ThrowingFileDeleteAdapter(),
            new ThrowingRegistryAdapter(),
            new ThrowingServiceAdapter(),
            new ThrowingTaskAdapter(),
            new ThrowingProcessAdapter(),
            new CopyAdapter());

        var restoreRunner = new MigrationRestoreRunner(new RecipePathResolver(rootsB), restoreGate);
        var stateStore = new RestoreStateStore();
        var restoreService = new MigrationRestoreService(restoreRunner, restoreExecutor, stateStore);
        MigrationRestoreExecutionResult restoreResult = restoreService.Restore(
            manifest,
            cfg.PackageDir,
            cfg.StateDir,
            utc,
            cfg.SelfTest ? "selftest-main" : null);
        MigrationRestorePlanResult restorePlan = restoreResult.PlanResult;

        Console.WriteLine($"[E2E]   restore plan actions : {restorePlan.Plan.Actions.Count}");
        Console.WriteLine($"[E2E]   restore plan skips   : {restorePlan.Skipped.Count}");
        foreach (var skip in restorePlan.Skipped)
            Console.WriteLine($"[E2E]     RESTORE-SKIP [{skip.Target.RecipeId}/{skip.Target.RelativePath}] ({skip.Reason}): {skip.Note}");

        evidence.RestorePlanSkips = restorePlan.Skipped
            .Select(s => new RestoreSkipEntry(s.Target.RecipeId, s.Target.RelativePath, s.Reason.ToString(), s.Note))
            .ToList();

        ExecutionReport execReport = restoreResult.Execution;
        Console.WriteLine($"[E2E]   execution authorized : {execReport.Authorized}");
        foreach (var r in execReport.Results)
            Console.WriteLine($"[E2E]     [{r.Kind}] {r.Status}  {r.Detail}");

        evidence.RestoreExecution = new ExecutionSummary(
            execReport.Authorized,
            execReport.Results.Select(r => new ActionResultEntry(r.Kind, r.Status.ToString(), r.Detail)).ToList());

        if (!execReport.Authorized)
        {
            Console.Error.WriteLine("[E2E] FAIL: restore execution was refused.");
            WriteReport(cfg.OutputDir, evidence, pass: false, "restore execution refused");
            return ExitVerifyFail;
        }

        // ------------------------------------------------------------------ 6. VERIFY
        Console.WriteLine("[E2E] Step 6: verification...");

        var hasher = new Sha256Hasher();
        var verifications = new List<VerificationEntry>();
        bool anyVerifyFail = false;

        // Only verify targets that were planned (i.e., passed restore filtering).
        var plannedEntryIds = new HashSet<string>(restorePlan.ActionEntryIds.Values, StringComparer.Ordinal);

        foreach (MigrationRestoreTarget target in manifest.Targets)
        {
            // Was this target excluded by the restore runner?
            bool wasSkipped = !plannedEntryIds.Contains(target.EntryId);
            if (wasSkipped)
            {
                RestoreSkip? skip = restorePlan.Skipped.FirstOrDefault(s => s.Target.EntryId == target.EntryId);
                string skipReason = skip is not null ? $"{skip.Reason}: {skip.Note}" : "skipped (unknown reason)";
                Console.WriteLine($"[E2E]   SKIPPED (not restored): {target.RecipeId}/{target.RelativePath} — {skipReason}");
                verifications.Add(new VerificationEntry(target.RecipeId, target.RelativePath,
                    Skipped: true, SkipReason: skipReason, ShaMatch: null, DestExists: null, ManifestSha: target.Sha256, RestoredSha: null));
                continue;
            }

            // Resolve the expected destination on Profile B.
            string destB;
            try
            {
                destB = pathsB.Resolve(target.KnownFolder, target.RelativePath);
            }
            catch (RecipePathException ex)
            {
                Console.Error.WriteLine($"[E2E]   FAIL (path resolve): {target.RecipeId}/{target.RelativePath}: {ex.Message}");
                verifications.Add(new VerificationEntry(target.RecipeId, target.RelativePath,
                    Skipped: false, SkipReason: null, ShaMatch: false, DestExists: false, ManifestSha: target.Sha256, RestoredSha: null,
                    DestPath: null, FailReason: $"path resolve: {ex.Message}"));
                anyVerifyFail = true;
                continue;
            }

            bool exists = File.Exists(destB);
            if (!exists)
            {
                Console.Error.WriteLine($"[E2E]   FAIL (missing): {target.RecipeId}/{target.RelativePath} expected at {destB}");
                verifications.Add(new VerificationEntry(target.RecipeId, target.RelativePath,
                    Skipped: false, SkipReason: null, ShaMatch: false, DestExists: false, ManifestSha: target.Sha256, RestoredSha: null,
                    DestPath: destB, FailReason: "destination file missing after restore"));
                anyVerifyFail = true;
                continue;
            }

            string restoredSha = hasher.ComputeFileSha256(destB);
            bool shaMatch = string.Equals(restoredSha, target.Sha256, StringComparison.Ordinal);
            if (shaMatch)
            {
                Console.WriteLine($"[E2E]   PASS: {target.RecipeId}/{target.RelativePath} -> {destB}  sha OK");
            }
            else
            {
                Console.Error.WriteLine($"[E2E]   FAIL (sha mismatch): {target.RecipeId}/{target.RelativePath}");
                Console.Error.WriteLine($"[E2E]     manifest sha: {target.Sha256}");
                Console.Error.WriteLine($"[E2E]     restored sha: {restoredSha}");
                anyVerifyFail = true;
            }

            verifications.Add(new VerificationEntry(target.RecipeId, target.RelativePath,
                Skipped: false, SkipReason: null, ShaMatch: shaMatch, DestExists: true,
                ManifestSha: target.Sha256, RestoredSha: restoredSha, DestPath: destB));
        }

        // FIX 3: require at least 2 verified+SHA-matched targets including both required recipes.
        int verifiedShaCount = verifications.Count(v => !v.Skipped && v.ShaMatch == true);
        var verifiedRecipes = new HashSet<string>(
            verifications.Where(v => !v.Skipped && v.ShaMatch == true).Select(v => v.RecipeId),
            StringComparer.OrdinalIgnoreCase);

        var missingRequired = RequiredRestoredRecipes
            .Where(r => !verifiedRecipes.Contains(r))
            .ToList();

        if (verifiedShaCount < 2 || missingRequired.Count > 0)
        {
            string msg = verifiedShaCount == 0
                ? "zero targets verified — non-vacuous round-trip requires at least git.config and anthropic.claude-code restored with SHA match"
                : $"required recipes not verified: {string.Join(", ", missingRequired)} (verified={verifiedShaCount})";
            Console.Error.WriteLine($"[E2E] FAIL: {msg}");
            anyVerifyFail = true;
        }

        // ------------------------------------------------------------------ 6b. EXCLUSION PROOF
        // FIX 2: POSITIVE pruning proof — assert seeded noise is NOT in the package.
        // For each seeded-noise file we scan the package dir recursively and FAIL if found.
        Console.WriteLine("[E2E] Step 6b: exclusion proof — asserting seeded noise is NOT in package...");

        // Canonical seeded-noise files (relative-to-profileA or relative-to-appDataA).
        // Each entry: (displayLabel, fileNameToSearch) — we search the PACKAGE dir recursively
        // for any file whose name matches.
        var noiseFileNames = new[]
        {
            // FIX 1 secrets inside .claude/skills/demo (a backed-up subtree)
            ("secret:id_rsa",        "id_rsa"),
            ("secret:app.secret",    "app.secret"),
            // Cache inside .claude/skills/demo/Cache (backed-up parent, Cache pruned)
            ("cache:blob.dat",       "blob.dat"),
            // Other cache / junk dirs
            ("cache:temp.dat",       "temp.dat"),
            ("cache:2026-06-21.snap","2026-06-21.snap"),
            ("cache:todo.txt",       "todo.txt"),
            ("cache:f_000001",       "f_000001"),
            ("cache:data_0",         "data_0"),
        };

        var exclusionResults = new List<ExclusionProofEntry>();
        bool anyExclusionLeak = false;

        foreach (var (label, name) in noiseFileNames)
        {
            // Search package dir recursively for this file name.
            string[] found = Directory.Exists(cfg.PackageDir)
                ? Directory.EnumerateFiles(cfg.PackageDir, name, SearchOption.AllDirectories).ToArray()
                : Array.Empty<string>();

            if (found.Length == 0)
            {
                Console.WriteLine($"[E2E]   PRUNED-FROM-PACKAGE: {label}  (not found in package — correct)");
                exclusionResults.Add(new ExclusionProofEntry(label, Leaked: false, LeakPaths: null));
            }
            else
            {
                Console.Error.WriteLine($"[E2E]   LEAK DETECTED: {label} found in package at:");
                foreach (string p in found)
                    Console.Error.WriteLine($"[E2E]     {p}");
                exclusionResults.Add(new ExclusionProofEntry(label, Leaked: true, LeakPaths: found.ToList()));
                anyExclusionLeak = true;
            }
        }

        evidence.ExclusionProof = exclusionResults;

        if (anyExclusionLeak)
        {
            Console.Error.WriteLine("[E2E] FAIL: one or more seeded noise/secret files leaked into the package.");
            anyVerifyFail = true;
        }

        evidence.Verifications = verifications;

        if (cfg.SelfTest)
        {
            Console.WriteLine("[E2E] Step 6c: selftest restore/undo assertions...");
            bool selfTestPass = RunSelfTestAssertions(
                cfg,
                manifest,
                pathsB,
                restoreResult,
                stateStore,
                restoreService,
                preRestoreSnapshots,
                evidence,
                utc);
            if (!selfTestPass)
                anyVerifyFail = true;
        }

        bool overallPass = !anyVerifyFail;

        // ------------------------------------------------------------------ 7. REPORT
        string verdict = overallPass ? "PASS" : "FAIL";
        Console.WriteLine();
        Console.WriteLine($"[E2E] ===== RESULT: {verdict} =====");
        Console.WriteLine($"[E2E] Git install     : {evidence.GitInstallStatus}");
        Console.WriteLine($"[E2E] VS Code install : {evidence.VsCodeInstallStatus}");
        Console.WriteLine("[E2E] NOTE: Round-trip PASS proves the migration engine over real config;");
        Console.WriteLine("[E2E]       it does NOT depend on the real installs succeeding (config is representative).");
        if (overallPass)
        {
            Console.WriteLine($"[E2E] Verified targets : {verifiedShaCount} (sha OK)");
            Console.WriteLine($"[E2E] Exclusion proof  : {exclusionResults.Count} noise files PRUNED-FROM-PACKAGE (none leaked)");
        }
        else
        {
            int failCount = verifications.Count(v => v.ShaMatch == false);
            int leakCount = exclusionResults.Count(e => e.Leaked);
            if (failCount > 0)
                Console.WriteLine($"[E2E] {failCount} SHA verification failure(s). See report for details.");
            if (leakCount > 0)
                Console.WriteLine($"[E2E] {leakCount} exclusion leak(s). See report for details.");
        }

        WriteReport(cfg.OutputDir, evidence, overallPass, overallPass ? null : "verification or exclusion failures");
        return overallPass ? ExitOk : ExitVerifyFail;
    }

    private static void WriteReport(string outputDir, EvidenceReport evidence, bool pass, string? failReason)
    {
        Directory.CreateDirectory(outputDir);

        evidence.Pass = pass;
        evidence.FailReason = failReason;
        evidence.GeneratedAt = DateTime.UtcNow.ToString("o");

        string jsonPath = Path.Combine(outputDir, "migration-e2e-evidence.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(evidence, JsonOptions), Encoding.UTF8);
        Console.WriteLine($"[E2E] JSON evidence: {jsonPath}");

        string summaryPath = Path.Combine(outputDir, "migration-e2e-summary.txt");
        var sb = new StringBuilder();
        sb.AppendLine("=== Migration E2E Round-Trip Summary ===");
        sb.AppendLine($"Generated : {evidence.GeneratedAt}");
        sb.AppendLine($"Result    : {(pass ? "PASS" : "FAIL")}");
        if (!pass && failReason is not null)
            sb.AppendLine($"Reason    : {failReason}");
        sb.AppendLine();
        sb.AppendLine($"Git install     : {evidence.GitInstallStatus}");
        sb.AppendLine($"VS Code install : {evidence.VsCodeInstallStatus}");
        sb.AppendLine("NOTE: Round-trip PASS proves the migration engine over real config;");
        sb.AppendLine("      it does NOT depend on the real installs succeeding (config is representative).");
        sb.AppendLine();
        sb.AppendLine($"Profile A : {evidence.Config?.ProfileA}");
        sb.AppendLine($"Profile B : {evidence.Config?.ProfileB}");
        sb.AppendLine($"Package   : {evidence.Config?.PackageDir}");
        sb.AppendLine();
        sb.AppendLine($"Recipes loaded : {evidence.RecipesLoaded?.Count ?? 0}");
        if (evidence.RecipesLoaded?.Count > 0)
            sb.AppendLine($"  {string.Join(", ", evidence.RecipesLoaded)}");
        sb.AppendLine();
        sb.AppendLine($"Backup authorized : {evidence.Backup?.Authorized}");
        sb.AppendLine($"Targets backed up : {evidence.Backup?.TargetCount}");
        if (evidence.Backup?.Skips?.Count > 0)
        {
            sb.AppendLine("Backup skips:");
            foreach (var s in evidence.Backup.Skips)
                sb.AppendLine($"  [{s.ItemPath}] {s.Reason}");
        }
        sb.AppendLine();
        sb.AppendLine($"Zip export: {evidence.ZipPath} ({evidence.ZipBytes:N0} bytes)");
        sb.AppendLine();
        sb.AppendLine($"Restore plan skips : {evidence.RestorePlanSkips?.Count ?? 0}");
        foreach (var rs in evidence.RestorePlanSkips ?? [])
            sb.AppendLine($"  [{rs.RecipeId}/{rs.RelativePath}] {rs.Reason}: {rs.Note}");
        sb.AppendLine();
        sb.AppendLine("Verifications:");
        foreach (var v in evidence.Verifications ?? [])
        {
            if (v.Skipped)
                sb.AppendLine($"  SKIPPED  {v.RecipeId}/{v.RelativePath}  ({v.SkipReason})");
            else if (v.ShaMatch == true)
                sb.AppendLine($"  PASS     {v.RecipeId}/{v.RelativePath}  sha={v.ManifestSha?[..8]}...");
            else
                sb.AppendLine($"  FAIL     {v.RecipeId}/{v.RelativePath}  reason={v.FailReason ?? "sha mismatch"}");
        }
        sb.AppendLine();
        sb.AppendLine("Exclusion proof (seeded noise must NOT appear in package):");
        foreach (var e in evidence.ExclusionProof ?? [])
        {
            if (!e.Leaked)
                sb.AppendLine($"  PRUNED-FROM-PACKAGE  {e.Label}");
            else
                sb.AppendLine($"  LEAKED               {e.Label}  at: {string.Join("; ", e.LeakPaths ?? [])}");
        }
        if (evidence.Dispositions is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Restore dispositions:");
            sb.AppendLine($"  Restored={evidence.Dispositions.RestoredCount} Reinstall={evidence.Dispositions.ReinstallEnqueuedCount} Manual={evidence.Dispositions.ManualCount}");
            sb.AppendLine($"  Skip-sourced manual={evidence.Dispositions.SkipSourcedManual}");
        }
        if (evidence.TierHonesty?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Tier honesty:");
            foreach (var t in evidence.TierHonesty)
                sb.AppendLine($"  {t.RecipeId}/{t.RelativePath} reason={t.SkipReason} destAbsent={t.DestAbsent} noBak={t.NoBakSiblings} noTmp={t.NoTmpSibling}");
        }
        if (evidence.Undo is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Undo proof:");
            sb.AppendLine($"  Undoable={evidence.Undo.UndoableJournalCount} NullBak={evidence.Undo.NullBakJournalCount} LoadedJournal={evidence.Undo.LoadedJournalCount} ExecutionFailures={evidence.Undo.ExecutionFailedCount}");
            foreach (var u in evidence.Undo.Entries)
                sb.AppendLine($"  {u.EntryId} bakMatch={u.BakMatchesOld} reverted={u.RevertedMatchesOld}");
        }
        if (evidence.MissingBak is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Missing .bak visible fail: {evidence.MissingBak.VisibleFailure} entry={evidence.MissingBak.EntryId}");
        }
        if (evidence.Resume is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Resume: run1={evidence.Resume.Run1JournalCount} run2={evidence.Resume.Run2JournalCount} alreadyDone={evidence.Resume.Run2AlreadyDoneSkip} undoSteps={evidence.Resume.UndoStepCount} reverted={evidence.Resume.RevertedMatchesOld}");
        }
        sb.AppendLine();
        sb.AppendLine(pass ? "=== PASS ===" : "=== FAIL ===");

        File.WriteAllText(summaryPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"[E2E] Summary     : {summaryPath}");
        Console.WriteLine();
        Console.Write(sb.ToString());
    }

    private static bool RunSelfTestAssertions(
        Config cfg,
        MigrationRestoreManifest manifest,
        RecipePathResolver pathsB,
        MigrationRestoreExecutionResult restoreResult,
        RestoreStateStore stateStore,
        MigrationRestoreService restoreService,
        IReadOnlyDictionary<string, FileSnapshot> preRestoreSnapshots,
        EvidenceReport evidence,
        DateTime utc)
    {
        bool pass = true;

        var skippedEntryIds = new HashSet<string>(
            restoreResult.PlanResult.Skipped.Select(s => s.Target.EntryId),
            StringComparer.OrdinalIgnoreCase);
        RestoreReportEntry? skipManual = restoreResult.RestoreReport.Manual
            .FirstOrDefault(m => skippedEntryIds.Contains(m.Id));
        evidence.Dispositions = new DispositionEvidence(
            restoreResult.RestoreReport.Restored.Count,
            restoreResult.RestoreReport.ReinstallEnqueued.Count,
            restoreResult.RestoreReport.Manual.Count,
            skipManual is not null,
            skipManual?.Id,
            skipManual?.RecipeId,
            skipManual?.Reason);

        if (restoreResult.RestoreReport.Restored.Count < 2 || skipManual is null)
        {
            Console.Error.WriteLine("[E2E] FAIL: 3-disposition proof is vacuous.");
            pass = false;
        }

        var tierEvidence = new List<TierHonestyEntry>();
        foreach (MigrationRestoreTarget target in manifest.Targets.Where(t =>
                     t.RecipeId is "google.chrome" || t.RecipeId == SyntheticInventoryRecipeId))
        {
            RestoreSkip? skip = restoreResult.PlanResult.Skipped.FirstOrDefault(s => s.Target.EntryId == target.EntryId);
            string? dest = TryResolve(pathsB, target.KnownFolder, target.RelativePath);
            bool destAbsent = dest is not null && !File.Exists(dest);
            bool noBak = dest is not null && NoSiblingMatches(dest, ".bak.*");
            bool noTmp = dest is not null && !File.Exists(dest + ".wcktmp");
            string expected = target.RecipeId == "google.chrome"
                ? RestoreSkipReason.MachineLocked.ToString()
                : RestoreSkipReason.InventoryOnly.ToString();
            bool reasonMatch = string.Equals(skip?.Reason.ToString(), expected, StringComparison.Ordinal);

            tierEvidence.Add(new TierHonestyEntry(
                target.RecipeId,
                target.EntryId,
                target.RelativePath,
                dest,
                skip?.Reason.ToString(),
                expected,
                destAbsent,
                noBak,
                noTmp,
                reasonMatch));

            if (!destAbsent || !noBak || !noTmp || !reasonMatch)
            {
                Console.Error.WriteLine($"[E2E] FAIL: blocked target wrote bytes or has wrong skip reason: {target.RecipeId}/{target.RelativePath}");
                pass = false;
            }
        }
        evidence.TierHonesty = tierEvidence;

        bool sawChrome = tierEvidence.Any(t => t.RecipeId == "google.chrome" && t.ReasonMatches);
        bool sawSynthetic = tierEvidence.Any(t => t.RecipeId == SyntheticInventoryRecipeId && t.ReasonMatches);
        if (!sawChrome || !sawSynthetic)
        {
            Console.Error.WriteLine("[E2E] FAIL: selftest did not exercise both Chrome MachineLocked and synthetic InventoryOnly targets.");
            pass = false;
        }

        RestoreState loadedState = stateStore.Load(cfg.StateDir);
        RestoreUndoPlan undoPlan = RestoreJournal.BuildUndoPlan(loadedState);
        var undoable = loadedState.Journal.Where(j => !string.IsNullOrWhiteSpace(j.BakPath)).ToArray();
        var nullBak = loadedState.Journal.Where(j => string.IsNullOrWhiteSpace(j.BakPath)).ToArray();
        var undoEntries = new List<UndoEntryEvidence>();

        if (undoable.Length < 2)
        {
            Console.Error.WriteLine("[E2E] FAIL: restore journal has fewer than 2 undoable .bak entries.");
            pass = false;
        }
        if (nullBak.Length < 1 || nullBak.Any(j => undoPlan.Steps.Any(s => string.Equals(s.EntryId, j.EntryId, StringComparison.OrdinalIgnoreCase))))
        {
            Console.Error.WriteLine("[E2E] FAIL: newly-created target is missing or incorrectly undo-planned.");
            pass = false;
        }
        if (tierEvidence.Any(t => undoPlan.Steps.Any(s => string.Equals(s.EntryId, t.EntryId, StringComparison.OrdinalIgnoreCase))))
        {
            Console.Error.WriteLine("[E2E] FAIL: blocked target appeared in undo plan.");
            pass = false;
        }

        foreach (RestoreJournalEntry entry in undoable)
        {
            preRestoreSnapshots.TryGetValue(entry.TargetPath, out FileSnapshot? old);
            string? restoredSha = File.Exists(entry.TargetPath) ? Sha256File(entry.TargetPath) : null;
            string? bakSha = entry.BakPath is not null && File.Exists(entry.BakPath) ? Sha256File(entry.BakPath) : null;
            bool bakMatchesOld = old?.Exists == true
                                 && string.Equals(bakSha, old.Sha256, StringComparison.OrdinalIgnoreCase);
            if (!bakMatchesOld)
            {
                Console.Error.WriteLine($"[E2E] FAIL: .bak does not match old Profile B bytes for {entry.EntryId}.");
                pass = false;
            }

            undoEntries.Add(new UndoEntryEvidence(
                entry.EntryId,
                entry.TargetPath,
                entry.BakPath,
                old?.Sha256,
                restoredSha,
                bakSha,
                bakMatchesOld,
                null,
                false));
        }

        MigrationRestoreUndoResult undo = restoreService.Undo(loadedState, utc.AddMinutes(1));
        int executionFailures = undo.Execution.Results.Count(r => r.Status == ActionStatus.Failed);
        var finalUndoEntries = new List<UndoEntryEvidence>();
        foreach (UndoEntryEvidence before in undoEntries)
        {
            preRestoreSnapshots.TryGetValue(before.TargetPath, out FileSnapshot? old);
            string? revertedSha = File.Exists(before.TargetPath) ? Sha256File(before.TargetPath) : null;
            bool revertedMatchesOld = old?.Exists == true
                                      && string.Equals(revertedSha, old.Sha256, StringComparison.OrdinalIgnoreCase);
            if (!revertedMatchesOld)
            {
                Console.Error.WriteLine($"[E2E] FAIL: undo did not restore old Profile B bytes for {before.EntryId}.");
                pass = false;
            }

            finalUndoEntries.Add(before with { RevertedSha = revertedSha, RevertedMatchesOld = revertedMatchesOld });
        }

        evidence.Undo = new UndoEvidence(
            loadedState.Journal.Count,
            undoable.Length,
            nullBak.Length,
            undoPlan.Steps.Count,
            undo.Execution.Authorized,
            executionFailures,
            undo.RejectedSteps.Count,
            finalUndoEntries);

        RestoreJournalEntry? missingBakEntry = undoable.FirstOrDefault();
        bool missingVisibleFail = false;
        int missingFailedCount = 0;
        int missingRejectedCount = 0;
        if (missingBakEntry?.BakPath is not null)
        {
            File.Delete(missingBakEntry.BakPath);
            MigrationRestoreUndoResult missingUndo = restoreService.Undo(loadedState, utc.AddMinutes(2));
            missingFailedCount = missingUndo.Execution.Results.Count(r => r.Status == ActionStatus.Failed);
            missingRejectedCount = missingUndo.RejectedSteps.Count;

            // Attribute the visible failure to the deleted-.bak entry SPECIFICALLY (auditor MINOR): a failure or
            // rejection elsewhere must NOT satisfy this proof. The undo action for the deleted entry is the one
            // whose Source == the deleted BakPath; assert THAT action failed (FileNotFoundException in Merge) or
            // that this exact step was rejected.
            var missingActionIds = missingUndo.BuildResult.Plan.Actions
                .OfType<RestoreMergeAction>()
                .Where(a => string.Equals(a.Source, missingBakEntry.BakPath, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Id)
                .ToHashSet(StringComparer.Ordinal);
            bool attributedFail =
                missingUndo.Execution.Results.Any(r => r.Status == ActionStatus.Failed && missingActionIds.Contains(r.ActionId))
                || missingUndo.RejectedSteps.Any(s => string.Equals(s.Step.BakPath, missingBakEntry.BakPath, StringComparison.OrdinalIgnoreCase));
            missingVisibleFail = attributedFail;
            if (!missingVisibleFail)
            {
                Console.Error.WriteLine("[E2E] FAIL: missing .bak undo did not surface a visible failure attributable to the deleted entry.");
                pass = false;
            }
        }
        evidence.MissingBak = new MissingBakEvidence(
            missingBakEntry?.EntryId,
            missingBakEntry?.BakPath,
            missingVisibleFail,
            missingFailedCount,
            missingRejectedCount);

        ResumeEvidence resume = RunResumeScenario(cfg, manifest, utc.AddMinutes(10));
        evidence.Resume = resume;
        if (!resume.Pass)
        {
            Console.Error.WriteLine("[E2E] FAIL: resume accumulation proof failed.");
            pass = false;
        }

        return pass;
    }

    private static ResumeEvidence RunResumeScenario(Config cfg, MigrationRestoreManifest manifest, DateTime utc)
    {
        MigrationRestoreTarget[] candidates = manifest.Targets
            .Where(t => t.PortabilityClass == PortabilityClass.ProfileRelative
                        && t.RestoreTier >= RestoreTier.ConfigCopy
                        && t.RestoreStrategy is RestoreStrategy.ConfigWrite or RestoreStrategy.MergeAfterInstall)
            .Take(2)
            .ToArray();
        if (candidates.Length < 2)
            return new ResumeEvidence(false, 0, 0, 0, false, 0, false);

        string profile = Path.Combine(cfg.SelfTestRoot!, "resume", "Users", "bob");
        string appData = Path.Combine(profile, "AppData", "Roaming");
        string local = Path.Combine(profile, "AppData", "Local");
        string stateDir = Path.Combine(cfg.SelfTestRoot!, "resume", "state");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(stateDir);

        var roots = new ProfileRoots(profile, appData, local);
        var resolver = new RecipePathResolver(roots);
        var old = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (MigrationRestoreTarget target in candidates)
        {
            string dest = resolver.Resolve(target.KnownFolder, target.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            string bytes = $"resume-old:{target.EntryId}";
            File.WriteAllText(dest, bytes, Encoding.UTF8);
            old[dest] = Sha256File(dest);
        }

        string usersRoot = Path.GetDirectoryName(profile) ?? profile;
        SafetyGate gate = BuildGate(profile, usersRoot);
        var service = new MigrationRestoreService(
            new MigrationRestoreRunner(resolver, gate),
            BuildExecutor(gate, "resume"),
            new RestoreStateStore());

        var run1Manifest = new MigrationRestoreManifest(MigrationRestoreManifest.CurrentSchemaVersion, new[] { candidates[0] });
        var run2Manifest = new MigrationRestoreManifest(MigrationRestoreManifest.CurrentSchemaVersion, candidates);

        service.Restore(run1Manifest, cfg.PackageDir, stateDir, utc, "resume-1");
        RestoreState afterRun1 = new RestoreStateStore().Load(stateDir);   // disk-grounded run1 journal (reboot sim)
        MigrationRestoreExecutionResult run2 = service.Restore(run2Manifest, cfg.PackageDir, stateDir, utc.AddMinutes(1), "resume-2");
        RestoreState loaded = new RestoreStateStore().Load(stateDir);      // disk-grounded final journal
        MigrationRestoreUndoResult undo = service.Undo(loaded, utc.AddMinutes(2));

        bool alreadyDone = run2.PlanResult.Skipped.Any(s =>
            s.Reason == RestoreSkipReason.AlreadyDone
            && string.Equals(s.Target.EntryId, candidates[0].EntryId, StringComparison.OrdinalIgnoreCase));
        // Accumulation proven purely from disk: run1's persisted journal had 1 entry; after run2 it has 2 (auditor MINOR).
        bool journalGrew = afterRun1.Journal.Count == 1 && loaded.Journal.Count == 2;
        bool undoCovered = undo.BuildResult.Plan.Actions.Count == 2;
        bool reverted = candidates.All(t =>
        {
            string dest = resolver.Resolve(t.KnownFolder, t.RelativePath);
            return old.TryGetValue(dest, out string? oldSha)
                   && string.Equals(Sha256File(dest), oldSha, StringComparison.OrdinalIgnoreCase);
        });

        return new ResumeEvidence(
            journalGrew && alreadyDone && undoCovered && reverted && undo.Execution.Results.All(r => r.Status == ActionStatus.Done),
            afterRun1.Journal.Count,
            run2.State.Journal.Count,
            loaded.Journal.Count,
            alreadyDone,
            undo.BuildResult.Plan.Actions.Count,
            reverted);
    }

    private static IReadOnlyDictionary<string, FileSnapshot> SnapshotTargets(
        MigrationRestoreManifest manifest,
        RecipePathResolver resolver)
    {
        var snapshots = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (MigrationRestoreTarget target in manifest.Targets)
        {
            string? path = TryResolve(resolver, target.KnownFolder, target.RelativePath);
            if (path is null || snapshots.ContainsKey(path))
                continue;
            snapshots[path] = File.Exists(path)
                ? new FileSnapshot(path, true, Sha256File(path))
                : new FileSnapshot(path, false, null);
        }
        return snapshots;
    }

    private static string? TryResolve(RecipePathResolver resolver, KnownFolder knownFolder, string relativePath)
    {
        try { return resolver.Resolve(knownFolder, relativePath); }
        catch (RecipePathException) { return null; }
    }

    private static bool NoSiblingMatches(string path, string suffixPattern)
    {
        string? dir = Path.GetDirectoryName(path);
        string leaf = Path.GetFileName(path);
        return string.IsNullOrEmpty(dir)
               || !Directory.Exists(dir)
               || !Directory.EnumerateFiles(dir, leaf + suffixPattern, SearchOption.TopDirectoryOnly).Any();
    }

    private static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static GatedExecutor BuildExecutor(SafetyGate gate, string label) =>
        new(
            gate,
            new ExecutionLog(
                Path.Combine(Path.GetTempPath(), $"wck-e2e-{label}-{Guid.NewGuid():N}.jsonl"),
                new LogRedactor(null, null)),
            new ThrowingFileDeleteAdapter(),
            new ThrowingRegistryAdapter(),
            new ThrowingServiceAdapter(),
            new ThrowingTaskAdapter(),
            new ThrowingProcessAdapter(),
            new CopyAdapter());

    // Build a SafetyGate that treats profileRoot as the current-user profile so
    // writes into its subtree are allowed (mirrors MigrationRestoreTestData.GateForProfile).
    private static SafetyGate BuildGate(string profileRoot, string usersRoot) =>
        new SafetyGate(
            new ProtectedResources(
                protectedDirectories: new[] { @"C:\Windows", @"C:\Program Files", @"C:\ProgramData" },
                windowsDirectory: @"C:\Windows",
                protectedProcessNames: ProtectedResources.DefaultProtectedProcessNames,
                criticalServiceNames: ProtectedResources.DefaultCriticalServiceNames,
                protectedRegistryKeys: ProtectedResources.DefaultProtectedRegistryKeys,
                wholeSubtreeRegistryRoots: ProtectedResources.DefaultWholeSubtreeRegistryRoots,
                commandDenyList: ProtectedResources.DefaultCommandDenyList,
                writeProtectedRoots: new[] { @"C:\Windows", @"C:\Program Files", @"C:\ProgramData" },
                usersRoot: usersRoot,
                currentUserProfile: profileRoot),
            new Win32PathCanonicalizer());

    private static bool TryParseSelfTestArgs(string[] args, out Config cfg, out string error)
    {
        cfg = default!;
        error = string.Empty;

        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--selftest", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!args[i].StartsWith("--"))
            {
                error = $"unexpected argument: {args[i]}";
                return false;
            }
            if (i + 1 >= args.Length)
            {
                error = $"missing value for {args[i]}";
                return false;
            }
            d[args[i].TrimStart('-')] = args[++i];
        }

        if (!d.TryGetValue("root", out string? root) || string.IsNullOrWhiteSpace(root))
        {
            error = "missing required argument: --root";
            return false;
        }

        string fullRoot = Path.GetFullPath(root);
        string workspace = Path.Combine(fullRoot, "migration-e2e-selftest");
        cfg = new Config(
            ProfileA: Path.Combine(workspace, "A", "Users", "alice"),
            AppDataA: Path.Combine(workspace, "A", "Users", "alice", "AppData", "Roaming"),
            LocalA: Path.Combine(workspace, "A", "Users", "alice", "AppData", "Local"),
            ProfileB: Path.Combine(workspace, "B", "Users", "bob"),
            AppDataB: Path.Combine(workspace, "B", "Users", "bob", "AppData", "Roaming"),
            LocalB: Path.Combine(workspace, "B", "Users", "bob", "AppData", "Local"),
            PackageDir: Path.Combine(workspace, "package"),
            OutputDir: Path.Combine(workspace, "output"),
            StateDir: Path.Combine(workspace, "state"),
            GitInstall: "not-run",
            VsCodeInstall: "not-run",
            SelfTest: true,
            SelfTestRoot: workspace);
        return true;
    }

    private static IReadOnlyList<MigrationRecipe> SeedSelfTest(Config cfg)
    {
        if (!cfg.SelfTest || string.IsNullOrWhiteSpace(cfg.SelfTestRoot))
            throw new InvalidOperationException("selftest seed requires a selftest config");

        PrepareCleanDirectory(cfg.SelfTestRoot);
        Directory.CreateDirectory(cfg.ProfileA);
        Directory.CreateDirectory(cfg.AppDataA);
        Directory.CreateDirectory(cfg.LocalA);
        Directory.CreateDirectory(cfg.ProfileB);
        Directory.CreateDirectory(cfg.AppDataB);
        Directory.CreateDirectory(cfg.LocalB);
        Directory.CreateDirectory(cfg.PackageDir);
        Directory.CreateDirectory(cfg.OutputDir);
        Directory.CreateDirectory(cfg.StateDir);

        WriteText(Path.Combine(cfg.ProfileA, ".gitconfig"), "[user]\n\tname = Alice Selftest\n\temail = alice@example.invalid\n");
        WriteText(Path.Combine(cfg.ProfileA, ".claude", "settings.json"), "{ \"theme\": \"dark\", \"model\": \"claude-sonnet\" }\n");
        WriteText(Path.Combine(cfg.ProfileA, ".claude", "CLAUDE.md"), "# Alice CLAUDE\n\nProject memory from Profile A.\n");
        WriteText(Path.Combine(cfg.ProfileA, ".claude", "projects", "demo", "memory", "note.md"), "# Demo memory\n");
        WriteText(Path.Combine(cfg.ProfileA, ".claude", "skills", "demo", "SKILL.md"), "# Demo skill\n");
        WriteText(Path.Combine(cfg.ProfileA, ".claude", "skills", "demo", "id_rsa"), "FAKE PRIVATE KEY\n");
        WriteText(Path.Combine(cfg.ProfileA, ".claude", "skills", "demo", "app.secret"), "FAKE APP SECRET TOKEN\n");
        WriteText(Path.Combine(cfg.ProfileA, ".claude", "skills", "demo", "Cache", "blob.dat"), "cache-blob-data\n");
        WriteText(Path.Combine(cfg.ProfileA, ".claude", "LocalCache", "temp.dat"), "cache-data\n");
        WriteText(Path.Combine(cfg.ProfileA, ".claude", "shell-snapshots", "2026-06-21.snap"), "snap\n");
        WriteText(Path.Combine(cfg.ProfileA, ".claude", "todos", "todo.txt"), "TODO\n");
        WriteText(Path.Combine(cfg.LocalA, "Google", "Chrome", "User Data", "Default", "Preferences"), "{ \"profile\": { \"name\": \"Alice\" } }\n");
        WriteText(Path.Combine(cfg.LocalA, "Google", "Chrome", "User Data", "Default", "Bookmarks"), "{ \"roots\": { \"bookmark_bar\": { \"children\": [] } } }\n");
        WriteText(Path.Combine(cfg.ProfileA, ".wck-inventory", "inventory.json"), "{ \"inventoryOnly\": true }\n");

        WriteText(Path.Combine(cfg.ProfileB, ".gitconfig"), "[user]\n\tname = Old Bob\n\temail = bob@example.invalid\n");
        WriteText(Path.Combine(cfg.ProfileB, ".claude", "settings.json"), "{ \"theme\": \"old\", \"model\": \"legacy\" }\n");

        var recipes = BuiltinRecipeSource.LoadAll()
            .Where(r => r.Id is "git.config" or "anthropic.claude-code" or "google.chrome")
            .ToList();
        recipes.Add(SyntheticInventoryRecipe());
        return recipes;
    }

    private static MigrationRecipe SyntheticInventoryRecipe() => new(
        SchemaVersion: 1,
        Id: SyntheticInventoryRecipeId,
        DisplayName: "WCK Synthetic Inventory Only",
        Category: "selftest",
        Detect: new RecipeDetect(KnownFolder.UserProfile, ".wck-inventory", Exists: true),
        Items: new[] { new RecipeItem(".wck-inventory/inventory.json", Array.Empty<string>(), Array.Empty<string>()) },
        Exclude: Array.Empty<string>(),
        SecretRule: "global",
        PortabilityClass: PortabilityClass.ProfileRelative,
        Restore: new RecipeRestore(RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, Array.Empty<string>()))
    {
        RestoreTier = RestoreTier.InventoryOnly,
        MigrationMeta = new MigrationRecipeMeta(
            UiWarning: null,
            ManualSteps: Array.Empty<string>(),
            ManualTodo: new[] { "Inventory-only selftest target must not restore automatically." },
            InstallerSource: InstallerSource.Unknown,
            LicenseSource: LicenseSource.None,
            RequiresRelogin: false,
            BackedUpButNotRestored: true,
            SurvivesOnOtherDrive: false),
    };

    private static void PrepareCleanDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        string localTemp = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp"));
        bool underTemp = IsContained(tempRoot, full)
                         || IsContained(localTemp, full);
        if (!underTemp)
            throw new InvalidOperationException($"selftest root must be under a temp directory: {full}");

        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
        Directory.CreateDirectory(full);
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
    }

    private static bool IsContained(string root, string candidate)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        string candidateFull = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               + Path.DirectorySeparatorChar;
        return candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseArgs(string[] args, out Config cfg, out string error)
    {
        cfg = default!;
        error = string.Empty;

        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length - 1; i += 2)
        {
            if (!args[i].StartsWith("--"))
            {
                error = $"unexpected argument: {args[i]}";
                return false;
            }
            if (i + 1 >= args.Length)
            {
                error = $"missing value for {args[i]}";
                return false;
            }
            d[args[i].TrimStart('-')] = args[i + 1];
        }

        string[] required = { "profileA", "appdataA", "localA", "profileB", "appdataB", "localB", "package", "output" };
        foreach (string key in required)
        {
            if (!d.ContainsKey(key))
            {
                error = $"missing required argument: --{key}";
                return false;
            }
        }

        cfg = new Config(
            ProfileA: d["profileA"],
            AppDataA: d["appdataA"],
            LocalA: d["localA"],
            ProfileB: d["profileB"],
            AppDataB: d["appdataB"],
            LocalB: d["localB"],
            PackageDir: d["package"],
            OutputDir: d["output"],
            StateDir: d.TryGetValue("state", out var state) ? state : Path.Combine(d["output"], "state"),
            GitInstall: d.TryGetValue("gitInstall", out var gi) ? gi : null,
            VsCodeInstall: d.TryGetValue("vscodeInstall", out var vs) ? vs : null);
        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage:
              MigrationE2E --selftest --root <temp-dir>

              MigrationE2E --profileA <root> --appdataA <path> --localA <path>
                           --profileB <root> --appdataB <path> --localB <path>
                           --package <dir> --output <dir>
                           [--gitInstall <ok|fail>] [--vscodeInstall <ok|fail>]

            Example (fabricated temp dirs):
              MigrationE2E \
                --profileA C:\Temp\A\Users\alice --appdataA C:\Temp\A\Users\alice\AppData\Roaming \
                --localA C:\Temp\A\Users\alice\AppData\Local \
                --profileB C:\Temp\B\Users\bob  --appdataB C:\Temp\B\AppData\bob\Roaming \
                --localB C:\Temp\B\AppData\bob\Local \
                --package C:\Temp\MigPkg --output C:\Temp\MigOut
            """);
    }
}

// ---------------------------------------------------------------------------
// Minimal stubs for adapters that must not be reached in a backup/restore plan.
// ---------------------------------------------------------------------------

internal sealed class ThrowingFileDeleteAdapter : IFileDeleteAdapter
{ public void Delete(FileDeleteAction a) => throw new InvalidOperationException("file delete not expected in migration E2E"); }

internal sealed class ThrowingRegistryAdapter : IRegistryAdapter
{ public void Delete(RegistryDeleteAction a) => throw new InvalidOperationException("registry delete not expected in migration E2E"); }

internal sealed class ThrowingServiceAdapter : IServiceAdapter
{ public void Apply(ServiceDeleteAction a) => throw new InvalidOperationException("service op not expected in migration E2E"); }

internal sealed class ThrowingTaskAdapter : ITaskAdapter
{ public void Apply(TaskDeleteAction a) => throw new InvalidOperationException("task op not expected in migration E2E"); }

internal sealed class ThrowingProcessAdapter : IProcessAdapter
{ public void Run(CommandAction a) => throw new InvalidOperationException("process run not expected in migration E2E"); }

// ---------------------------------------------------------------------------
// Data contracts for the evidence report.
// ---------------------------------------------------------------------------

internal sealed record Config(
    string ProfileA, string AppDataA, string LocalA,
    string ProfileB, string AppDataB, string LocalB,
    string PackageDir, string OutputDir, string StateDir,
    string? GitInstall = null,
    string? VsCodeInstall = null,
    bool SelfTest = false,
    string? SelfTestRoot = null);

internal sealed class EvidenceReport
{
    public bool Pass { get; set; }
    public string? FailReason { get; set; }
    public string? GeneratedAt { get; set; }
    public ConfigSummary? Config { get; set; }
    public string? GitInstallStatus { get; set; }
    public string? VsCodeInstallStatus { get; set; }
    public List<string>? RecipesLoaded { get; set; }
    public BackupSummary? Backup { get; set; }
    public string? ZipPath { get; set; }
    public long ZipBytes { get; set; }
    public List<RestoreSkipEntry>? RestorePlanSkips { get; set; }
    public ExecutionSummary? RestoreExecution { get; set; }
    public List<VerificationEntry>? Verifications { get; set; }
    public List<ExclusionProofEntry>? ExclusionProof { get; set; }
    public DispositionEvidence? Dispositions { get; set; }
    public List<TierHonestyEntry>? TierHonesty { get; set; }
    public UndoEvidence? Undo { get; set; }
    public MissingBakEvidence? MissingBak { get; set; }
    public ResumeEvidence? Resume { get; set; }
}

internal sealed record ConfigSummary(
    string ProfileA, string AppDataA, string LocalA,
    string ProfileB, string AppDataB, string LocalB,
    string PackageDir, string OutputDir, string StateDir);

internal sealed record BackupSummary(
    bool Authorized,
    int TargetCount,
    List<SkipEntry> Skips,
    List<SkipEntry> FinalizationSkips,
    List<TargetEntry> Targets);

internal sealed record SkipEntry(string ItemPath, string Reason);

internal sealed record TargetEntry(string RecipeId, string KnownFolder, string RelativePath, string Sha256);

internal sealed record RestoreSkipEntry(string RecipeId, string RelativePath, string Reason, string Note);

internal sealed record ExecutionSummary(bool Authorized, List<ActionResultEntry> Results);

internal sealed record ActionResultEntry(string Kind, string Status, string Detail);

internal sealed record VerificationEntry(
    string RecipeId,
    string RelativePath,
    bool Skipped,
    string? SkipReason,
    bool? ShaMatch,
    bool? DestExists,
    string? ManifestSha,
    string? RestoredSha,
    string? DestPath = null,
    string? FailReason = null);

internal sealed record ExclusionProofEntry(
    string Label,
    bool Leaked,
    List<string>? LeakPaths);

internal sealed record FileSnapshot(string Path, bool Exists, string? Sha256);

internal sealed record DispositionEvidence(
    int RestoredCount,
    int ReinstallEnqueuedCount,
    int ManualCount,
    bool SkipSourcedManual,
    string? SkipManualId,
    string? SkipManualRecipeId,
    string? SkipManualReason);

internal sealed record TierHonestyEntry(
    string RecipeId,
    string EntryId,
    string RelativePath,
    string? DestinationPath,
    string? SkipReason,
    string ExpectedReason,
    bool DestAbsent,
    bool NoBakSiblings,
    bool NoTmpSibling,
    bool ReasonMatches);

internal sealed record UndoEvidence(
    int LoadedJournalCount,
    int UndoableJournalCount,
    int NullBakJournalCount,
    int UndoStepCount,
    bool ExecutionAuthorized,
    int ExecutionFailedCount,
    int RejectedStepCount,
    List<UndoEntryEvidence> Entries);

internal sealed record UndoEntryEvidence(
    string EntryId,
    string TargetPath,
    string? BakPath,
    string? OldSha,
    string? RestoredSha,
    string? BakSha,
    bool BakMatchesOld,
    string? RevertedSha,
    bool RevertedMatchesOld);

internal sealed record MissingBakEvidence(
    string? EntryId,
    string? BakPath,
    bool VisibleFailure,
    int FailedActionCount,
    int RejectedStepCount);

internal sealed record ResumeEvidence(
    bool Pass,
    int Run1JournalCount,
    int Run2JournalCount,
    int LoadedJournalCount,
    bool Run2AlreadyDoneSkip,
    int UndoStepCount,
    bool RevertedMatchesOld);

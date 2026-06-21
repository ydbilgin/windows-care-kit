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

    internal static int Main(string[] args)
    {
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

    private static int Run(Config cfg)
    {
        var evidence = new EvidenceReport();
        evidence.Config = new ConfigSummary(cfg.ProfileA, cfg.AppDataA, cfg.LocalA,
                                            cfg.ProfileB, cfg.AppDataB, cfg.LocalB,
                                            cfg.PackageDir, cfg.OutputDir);
        evidence.GitInstallStatus = cfg.GitInstall ?? "not-reported";
        evidence.VsCodeInstallStatus = cfg.VsCodeInstall ?? "not-reported";

        // ------------------------------------------------------------------ 1. LOAD BUILTIN RECIPES
        Console.WriteLine("[E2E] Step 1: loading builtin recipes...");
        IReadOnlyList<MigrationRecipe> recipes = BuiltinRecipeSource.LoadAll();
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
        MigrationRestorePlanResult restorePlan = restoreRunner.BuildPlan(manifest, cfg.PackageDir, RestoreState.Empty, utc);

        Console.WriteLine($"[E2E]   restore plan actions : {restorePlan.Plan.Actions.Count}");
        Console.WriteLine($"[E2E]   restore plan skips   : {restorePlan.Skipped.Count}");
        foreach (var skip in restorePlan.Skipped)
            Console.WriteLine($"[E2E]     RESTORE-SKIP [{skip.Target.RecipeId}/{skip.Target.RelativePath}] ({skip.Reason}): {skip.Note}");

        evidence.RestorePlanSkips = restorePlan.Skipped
            .Select(s => new RestoreSkipEntry(s.Target.RecipeId, s.Target.RelativePath, s.Reason.ToString(), s.Note))
            .ToList();

        ExecutionReport execReport = restoreExecutor.ExecuteWithReport(restorePlan.Plan, restorePlan.Plan.ComputeHash());
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
        var pathsB = new RecipePathResolver(rootsB);
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
        sb.AppendLine();
        sb.AppendLine(pass ? "=== PASS ===" : "=== FAIL ===");

        File.WriteAllText(summaryPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"[E2E] Summary     : {summaryPath}");
        Console.WriteLine();
        Console.Write(sb.ToString());
    }

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
            GitInstall: d.TryGetValue("gitInstall", out var gi) ? gi : null,
            VsCodeInstall: d.TryGetValue("vscodeInstall", out var vs) ? vs : null);
        return true;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage:
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
    string PackageDir, string OutputDir,
    string? GitInstall = null,
    string? VsCodeInstall = null);

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
}

internal sealed record ConfigSummary(
    string ProfileA, string AppDataA, string LocalA,
    string ProfileB, string AppDataB, string LocalB,
    string PackageDir, string OutputDir);

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

using System.Diagnostics;
using System.Security.Cryptography;
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

namespace WindowsCareKit.Tools.MigrationRealRestore;

internal static class Program
{
    private const string NoncePath = @"C:\WCK-Campaign\guest-nonce.txt";

    private const int ExitOk = 0;
    private const int ExitGateFail = 1;
    private const int ExitUsage = 2;
    private const int ExitNonceRefusal = 3;
    private const int ExitEnvironmentRefusal = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly string[] RestorableRecipeIds =
    {
        "git.config",
        "microsoft.vscode",
        "notepadplusplus.notepadplusplus",
    };

    public static int Main(string[] args)
    {
        if (!TryParseArgs(args, out Config cfg, out string parseError))
        {
            Console.Error.WriteLine($"[RealRestore] ERROR: {parseError}");
            PrintUsage();
            return ExitUsage;
        }

        NonceCheck nonce = CheckGuestNonce(cfg.RequiredGuestNonce);
        if (!nonce.Pass)
        {
            Console.Error.WriteLine($"[RealRestore] nonce refusal: {nonce.Reason}");
            return ExitNonceRefusal;
        }

        ProfileRoots roots = ProfileRoots.ForCurrentUser();
        EnvironmentCheck environment = CheckEnvironment(roots, cfg.ExpectProfileRoot);
        if (!environment.Pass)
        {
            Console.Error.WriteLine($"[RealRestore] environment refusal: {environment.Reason}");
            return ExitEnvironmentRefusal;
        }

        try
        {
            return cfg.Mode switch
            {
                Mode.Backup => RunBackup(cfg, roots),
                Mode.Fingerprint => RunFingerprint(cfg, roots),
                Mode.Restore => RunRestore(cfg, roots),
                Mode.Verify => RunVerify(cfg, roots),
                Mode.Undo => RunUndo(cfg, roots),
                _ => ExitUsage,
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"[RealRestore] usage error: {ex.Message}");
            return ExitUsage;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or MigrationManifestException
                                   or RecipePathException
                                   or JsonException
                                   or InvalidOperationException)
        {
            Console.Error.WriteLine($"[RealRestore] FAIL: {ex.Message}");
            WriteEvidence(cfg.OutputDir, cfg.Mode, new FailureEvidence(ex.GetType().Name, ex.Message), pass: false);
            return ExitGateFail;
        }
    }

    private static int RunBackup(Config cfg, ProfileRoots roots)
    {
        DateTime utc = DateTime.UtcNow;
        IReadOnlyList<MigrationRecipe> recipes = LoadRequestedRecipes(cfg.Recipes);

        SafetyGate backupGate = BuildGate(
            profileRoot: cfg.PackageDir!,
            usersRoot: cfg.PackageDir!);

        var backupRunner = new MigrationBackupRunner(
            new RecipeResolver(new RecipePathResolver(roots), new Win32RecipeFileSystem()),
            new BackupExecutorAdapter(BuildExecutor(backupGate, "backup")),
            new Sha256Hasher(),
            new PhysicalFileSystem(),
            new MigrationRestoreManifestStore(),
            backupGate);

        MigrationBackupPlanResult plan = backupRunner.BuildPlan(recipes, cfg.PackageDir!, utc);
        MigrationBackupRunResult run = backupRunner.Run(plan, plan.Plan.ComputeHash(), cfg.PackageDir!);

        List<PruneProofEntry> pruneProof = BuildPruneProof(cfg.PackageDir!, cfg.AssertPruned);
        bool noPruneLeaks = pruneProof.All(p => p.Hits.Count == 0);
        bool hasRestorableTargets = RestorableRecipeIds.All(id =>
            run.Manifest.Targets.Any(t => string.Equals(t.RecipeId, id, StringComparison.OrdinalIgnoreCase)));
        bool hasChromeTarget = run.Manifest.Targets.Any(t =>
            string.Equals(t.RecipeId, "google.chrome", StringComparison.OrdinalIgnoreCase));

        bool pass = run.Authorized
                    && hasRestorableTargets
                    && hasChromeTarget
                    && noPruneLeaks;

        var evidence = new BackupEvidence(
            run.Authorized,
            plan.Plan.Actions.Count,
            plan.SkippedItems.Select(ToSkipEntry).ToArray(),
            run.SkippedItems.Select(ToSkipEntry).ToArray(),
            run.FinalizationSkips.Select(ToSkipEntry).ToArray(),
            run.Manifest.Targets.Select(ToTargetEntry).ToArray(),
            pruneProof,
            new BackupGates(hasRestorableTargets, hasChromeTarget, noPruneLeaks));

        WriteEvidence(cfg.OutputDir, cfg.Mode, evidence, pass);
        Console.WriteLine($"[RealRestore] backup pass={pass} authorized={run.Authorized} targets={run.Manifest.Targets.Count}");
        return pass ? ExitOk : ExitGateFail;
    }

    private static int RunFingerprint(Config cfg, ProfileRoots roots)
    {
        MigrationRestoreManifest manifest = new MigrationRestoreManifestStore().Load(cfg.PackageDir!);
        var resolver = new RecipePathResolver(roots);
        var entries = new List<FingerprintEntry>();

        foreach (MigrationRestoreTarget target in manifest.Targets)
        {
            string? dest = TryResolve(resolver, target.KnownFolder, target.RelativePath, out string? error);
            bool exists = dest is not null && File.Exists(dest);
            entries.Add(new FingerprintEntry(
                target.RecipeId,
                target.EntryId,
                target.RelativePath,
                dest,
                error,
                exists,
                exists && dest is not null ? Sha256File(dest) : null));
        }

        GitReadBack git = ReadGitEmail();
        var evidence = new FingerprintEvidence(entries, git);
        WriteEvidence(cfg.OutputDir, cfg.Mode, evidence, pass: true);
        Console.WriteLine($"[RealRestore] fingerprint pass=True targets={entries.Count}");
        return ExitOk;
    }

    private static int RunRestore(Config cfg, ProfileRoots roots)
    {
        DateTime utc = DateTime.UtcNow;
        MigrationRestoreManifest manifest = new MigrationRestoreManifestStore().Load(cfg.PackageDir!);
        MigrationRestoreService restoreService = BuildRestoreService(roots, cfg.StateDir!, "restore");

        MigrationRestorePreviewResult preview = restoreService.Preview(
            manifest,
            cfg.PackageDir!,
            cfg.StateDir!,
            utc);
        MigrationRestoreExecutionResult restore = restoreService.Restore(
            manifest,
            cfg.PackageDir!,
            cfg.StateDir!,
            utc,
            runToken: "realmig",
            approvedHash: preview.PlanHash);

        HashSet<string> chromeEntryIds = manifest.Targets
            .Where(t => string.Equals(t.RecipeId, "google.chrome", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.EntryId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool allChromeManual = chromeEntryIds.Count > 0
                               && chromeEntryIds.All(id => restore.RestoreReport.Manual.Any(m =>
                                   string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(m.Reason, RestoreSkipReason.MachineLocked.ToString(), StringComparison.OrdinalIgnoreCase)));
        bool noChromeRestored = restore.RestoreReport.Restored.All(r => !chromeEntryIds.Contains(r.Id));
        bool enoughRestored = restore.RestoreReport.Restored.Count >= 3;
        bool noFailedActions = restore.Execution.Results.All(r =>
            r.Status is not (ActionStatus.Failed or ActionStatus.Blocked));
        bool pass = restore.Execution.Authorized
                    && allChromeManual
                    && noChromeRestored
                    && enoughRestored
                    && noFailedActions;

        var evidence = new RestoreEvidence(
            restore.Authorized,
            restore.Execution.Authorized,
            preview.PlanHash,
            restore.Execution.PlanHash,
            restore.PlanResult.Plan.Actions.Count,
            restore.PlanResult.Skipped.Select(ToRestoreSkipEntry).ToArray(),
            restore.Execution.Results.Select(ToActionEntry).ToArray(),
            ToReportEvidence(restore.RestoreReport),
            new RestoreGates(allChromeManual, noChromeRestored, enoughRestored, noFailedActions));

        WriteEvidence(cfg.OutputDir, cfg.Mode, evidence, pass);
        Console.WriteLine($"[RealRestore] restore pass={pass} authorized={restore.Execution.Authorized} restored={restore.RestoreReport.Restored.Count}");
        return pass ? ExitOk : ExitGateFail;
    }

    private static int RunVerify(Config cfg, ProfileRoots roots)
    {
        MigrationRestoreManifest manifest = new MigrationRestoreManifestStore().Load(cfg.PackageDir!);
        RestoreState state = new RestoreStateStore().Load(cfg.StateDir!);
        FingerprintEvidence? baseline = LoadBaseline(cfg.BaselinePath!);
        var resolver = new RecipePathResolver(roots);

        HashSet<string> restoredEntryIds = state.Entries
            .Where(e => e.Status == RestoreEntryStatus.Done)
            .Select(e => e.EntryId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var restoredChecks = new List<VerifyRestoredEntry>();
        foreach (MigrationRestoreTarget target in manifest.Targets.Where(t => restoredEntryIds.Contains(t.EntryId)))
        {
            string? dest = TryResolve(resolver, target.KnownFolder, target.RelativePath, out string? error);
            bool exists = dest is not null && File.Exists(dest);
            string? actualSha = exists && dest is not null ? Sha256File(dest) : null;
            bool shaMatch = exists && string.Equals(actualSha, target.Sha256, StringComparison.OrdinalIgnoreCase);
            restoredChecks.Add(new VerifyRestoredEntry(
                target.RecipeId,
                target.EntryId,
                target.RelativePath,
                dest,
                error,
                exists,
                target.Sha256,
                actualSha,
                shaMatch));
        }

        GitReadBack git = ReadGitEmail();
        bool gitPass = string.Equals(git.StdOut?.Trim(), cfg.ExpectGitEmail, StringComparison.Ordinal);

        IReadOnlyList<MachineLockedCheck> machineLocked = BuildMachineLockedChecks(manifest, resolver, baseline);
        IReadOnlyList<AppConsumptionEntry> appConsumption = RunBestEffortAppConsumptionChecks(restoredChecks);

        bool restoredPass = restoredChecks.Count > 0 && restoredChecks.All(v => v.ShaMatch);
        bool machineLockedPass = machineLocked.All(m => m.Pass);
        bool pass = restoredPass && gitPass && machineLockedPass;

        var evidence = new VerifyEvidence(
            restoredChecks,
            new GitVerifyEvidence(cfg.ExpectGitEmail, git.StdOut?.Trim(), git.ExitCode, git.Executable, gitPass),
            machineLocked,
            appConsumption,
            new VerifyGates(restoredPass, gitPass, machineLockedPass));

        WriteEvidence(cfg.OutputDir, cfg.Mode, evidence, pass);
        Console.WriteLine($"[RealRestore] verify pass={pass} restoredChecks={restoredChecks.Count} gitPass={gitPass}");
        return pass ? ExitOk : ExitGateFail;
    }

    private static int RunUndo(Config cfg, ProfileRoots roots)
    {
        DateTime utc = DateTime.UtcNow;
        var stateStore = new RestoreStateStore();
        RestoreState state = stateStore.Load(cfg.StateDir!);
        MigrationRestoreService restoreService = BuildRestoreService(roots, cfg.StateDir!, "undo");

        MigrationRestoreUndoPreviewResult preview = restoreService.PreviewUndo(state, utc);
        MigrationRestoreUndoResult undo = restoreService.Undo(
            state,
            cfg.StateDir!,
            utc,
            approvedUndoHash: preview.PlanHash);

        var entries = new List<UndoEntryEvidence>();
        foreach (RestoreJournalEntry journal in state.Journal)
        {
            if (!string.IsNullOrWhiteSpace(journal.BakPath))
            {
                bool exists = File.Exists(journal.TargetPath);
                string? revertedSha = exists ? Sha256File(journal.TargetPath) : null;
                bool match = !string.IsNullOrWhiteSpace(journal.ShaBefore)
                             && string.Equals(revertedSha, journal.ShaBefore, StringComparison.OrdinalIgnoreCase);
                entries.Add(new UndoEntryEvidence(
                    journal.EntryId,
                    journal.TargetPath,
                    journal.BakPath,
                    journal.ShaBefore,
                    revertedSha,
                    exists,
                    match,
                    null,
                    null));
                continue;
            }

            RejectedRestoreUndoStep? rejected = undo.RejectedSteps.FirstOrDefault(r =>
                string.Equals(r.Step.EntryId, journal.EntryId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Step.TargetPath, journal.TargetPath, StringComparison.OrdinalIgnoreCase));
            bool rejectedCreated = rejected is not null
                                   && rejected.Reason.Contains("created", StringComparison.OrdinalIgnoreCase);
            bool stillExists = File.Exists(journal.TargetPath);
            entries.Add(new UndoEntryEvidence(
                journal.EntryId,
                journal.TargetPath,
                null,
                journal.ShaBefore,
                stillExists ? Sha256File(journal.TargetPath) : null,
                stillExists,
                null,
                rejected?.Reason,
                rejectedCreated));
        }

        GitReadBack gitAfter = ReadGitEmail();
        bool bakPass = entries.Where(e => !string.IsNullOrWhiteSpace(e.BakPath))
            .All(e => e.RevertedMatchesBefore == true);
        bool createdPass = entries.Where(e => string.IsNullOrWhiteSpace(e.BakPath))
            .All(e => e.RejectedReasonContainsCreated == true && e.TargetExistsAfterUndo);
        bool noFailedActions = undo.Execution.Results.All(r =>
            r.Status is not (ActionStatus.Failed or ActionStatus.Blocked));
        bool pass = undo.Execution.Authorized && bakPass && createdPass && noFailedActions;

        var evidence = new UndoEvidence(
            undo.Authorized,
            undo.Execution.Authorized,
            preview.PlanHash,
            undo.Execution.PlanHash,
            undo.BuildResult.Plan.Actions.Count,
            undo.RejectedSteps.Select(ToRejectedUndoEntry).ToArray(),
            undo.Execution.Results.Select(ToActionEntry).ToArray(),
            entries,
            new GitReadBackEvidence(gitAfter.StdOut?.Trim(), gitAfter.ExitCode, gitAfter.Executable),
            new UndoGates(bakPass, createdPass, noFailedActions));

        WriteEvidence(cfg.OutputDir, cfg.Mode, evidence, pass);
        Console.WriteLine($"[RealRestore] undo pass={pass} journalEntries={state.Journal.Count}");
        return pass ? ExitOk : ExitGateFail;
    }

    private static MigrationRestoreService BuildRestoreService(ProfileRoots roots, string stateDir, string label)
    {
        string usersRoot = Path.GetDirectoryName(roots.UserProfile) ?? roots.UserProfile;
        SafetyGate restoreGate = BuildGate(roots.UserProfile, usersRoot);
        var runner = new MigrationRestoreRunner(new RecipePathResolver(roots), restoreGate);
        return new MigrationRestoreService(runner, BuildExecutor(restoreGate, label), new RestoreStateStore());
    }

    private static GatedExecutor BuildExecutor(SafetyGate gate, string label) =>
        new(
            gate,
            new ExecutionLog(
                Path.Combine(Path.GetTempPath(), $"wck-realmig-{label}-{Guid.NewGuid():N}.jsonl"),
                new LogRedactor(null, null)),
            new ThrowingFileDeleteAdapter(),
            new ThrowingRegistryAdapter(),
            new ThrowingServiceAdapter(),
            new ThrowingTaskAdapter(),
            new ThrowingProcessAdapter(),
            new CopyAdapter());

    private static SafetyGate BuildGate(string profileRoot, string usersRoot) =>
        new(
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

    private static IReadOnlyList<MigrationRecipe> LoadRequestedRecipes(IReadOnlyList<string> requestedIds)
    {
        IReadOnlyList<MigrationRecipe> all = BuiltinRecipeSource.LoadAll();
        var byId = all.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        var selected = new List<MigrationRecipe>(requestedIds.Count);

        foreach (string id in requestedIds)
        {
            if (!byId.TryGetValue(id, out MigrationRecipe? recipe))
                throw new UsageException($"unknown recipe id: {id}");
            selected.Add(recipe);
        }

        if (selected.Count == 0)
            throw new UsageException("at least one recipe id is required");

        return selected;
    }

    private static IReadOnlyList<MachineLockedCheck> BuildMachineLockedChecks(
        MigrationRestoreManifest manifest,
        RecipePathResolver resolver,
        FingerprintEvidence? baseline)
    {
        var byEntryId = (baseline?.Entries ?? Array.Empty<FingerprintEntry>())
            .ToDictionary(e => e.EntryId, StringComparer.OrdinalIgnoreCase);
        var checks = new List<MachineLockedCheck>();

        foreach (MigrationRestoreTarget target in manifest.Targets.Where(t => t.PortabilityClass != PortabilityClass.ProfileRelative))
        {
            string? dest = TryResolve(resolver, target.KnownFolder, target.RelativePath, out string? error);
            bool exists = dest is not null && File.Exists(dest);
            string? sha = exists && dest is not null ? Sha256File(dest) : null;
            byEntryId.TryGetValue(target.EntryId, out FingerprintEntry? before);
            bool same = before is not null
                        && before.Exists == exists
                        && string.Equals(before.Sha256, sha, StringComparison.OrdinalIgnoreCase);
            bool noBak = dest is null || NoSiblingMatches(dest, ".bak.*");
            bool noTmp = dest is null || !File.Exists(dest + ".wcktmp");
            checks.Add(new MachineLockedCheck(
                target.RecipeId,
                target.EntryId,
                target.RelativePath,
                dest,
                error,
                before?.Exists,
                before?.Sha256,
                exists,
                sha,
                same,
                noBak,
                noTmp,
                same && noBak && noTmp));
        }

        return checks;
    }

    private static IReadOnlyList<AppConsumptionEntry> RunBestEffortAppConsumptionChecks(
        IReadOnlyList<VerifyRestoredEntry> restored)
    {
        var result = new List<AppConsumptionEntry>();
        TryAppProbe(
            "notepadplusplus.notepadplusplus",
            "notepad++.exe",
            new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Notepad++", "notepad++.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Notepad++", "notepad++.exe"),
            },
            restored,
            result);
        TryAppProbe(
            "microsoft.vscode",
            "Code.exe",
            new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
            },
            restored,
            result);
        return result;
    }

    private static void TryAppProbe(
        string recipeId,
        string processName,
        IReadOnlyList<string> executableCandidates,
        IReadOnlyList<VerifyRestoredEntry> restored,
        List<AppConsumptionEntry> result)
    {
        VerifyRestoredEntry[] entries = restored
            .Where(e => string.Equals(e.RecipeId, recipeId, StringComparison.OrdinalIgnoreCase)
                        && e.DestPath is not null
                        && File.Exists(e.DestPath))
            .ToArray();
        if (entries.Length == 0)
            return;

        string? exe = executableCandidates.FirstOrDefault(File.Exists);
        if (exe is null)
        {
            result.Add(new AppConsumptionEntry(recipeId, null, false, "executable not found", Array.Empty<AppFileProbe>()));
            return;
        }

        AppFileProbe[] before = entries.Select(e => new AppFileProbe(
            e.EntryId,
            e.DestPath!,
            File.Exists(e.DestPath!) ? Sha256File(e.DestPath!) : null,
            null,
            null)).ToArray();
        string? error = null;
        bool launched = false;

        try
        {
            using Process proc = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            launched = true;
            if (!proc.WaitForExit(3000))
            {
                try { proc.CloseMainWindow(); }
                catch (InvalidOperationException) { }
                if (!proc.WaitForExit(3000))
                    proc.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        finally
        {
            string procLeaf = Path.GetFileNameWithoutExtension(processName);
            foreach (Process p in Process.GetProcessesByName(procLeaf))
            {
                try { p.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }

        AppFileProbe[] after = before.Select(b =>
        {
            string? afterSha = File.Exists(b.Path) ? Sha256File(b.Path) : null;
            return b with
            {
                ShaAfter = afterSha,
                ResetByApp = b.ShaBefore is not null
                             && afterSha is not null
                             && !string.Equals(b.ShaBefore, afterSha, StringComparison.OrdinalIgnoreCase),
            };
        }).ToArray();

        result.Add(new AppConsumptionEntry(recipeId, exe, launched, error, after));
    }

    private static FingerprintEvidence? LoadBaseline(string path)
    {
        using FileStream stream = File.OpenRead(path);
        EvidenceEnvelope<FingerprintEvidence>? envelope =
            JsonSerializer.Deserialize<EvidenceEnvelope<FingerprintEvidence>>(stream, JsonOptions);
        return envelope?.Details;
    }

    private static NonceCheck CheckGuestNonce(string requiredNonce)
    {
        if (!File.Exists(NoncePath))
            return new NonceCheck(false, $"missing {NoncePath}");

        string content;
        try
        {
            content = File.ReadLines(NoncePath).FirstOrDefault() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new NonceCheck(false, $"cannot read {NoncePath}: {ex.Message}");
        }

        string[] parts = content.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !Guid.TryParse(parts[0], out Guid fileGuid)
            || !Guid.TryParse(requiredNonce, out Guid requiredGuid))
        {
            return new NonceCheck(false, "nonce content is malformed");
        }

        if (fileGuid != requiredGuid)
            return new NonceCheck(false, "nonce guid mismatch");
        if (!string.Equals(parts[1], Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return new NonceCheck(false, "nonce computer-name mismatch");
        return new NonceCheck(true, null);
    }

    private static EnvironmentCheck CheckEnvironment(ProfileRoots roots, string expectedProfileRoot)
    {
        string actualProfile = NormalizePath(roots.UserProfile);
        string expectedProfile = NormalizePath(expectedProfileRoot);
        if (!string.Equals(actualProfile, expectedProfile, StringComparison.OrdinalIgnoreCase))
        {
            return new EnvironmentCheck(false,
                $"profile root mismatch: actual '{roots.UserProfile}', expected '{expectedProfileRoot}'");
        }

        foreach (string root in new[] { roots.UserProfile, roots.AppData, roots.LocalAppData })
        {
            if (HasOneDriveSegment(root))
                return new EnvironmentCheck(false, $"OneDrive path segment refused: {root}");
        }

        string? oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrWhiteSpace(oneDrive) && IsContained(oneDrive, roots.UserProfile))
            return new EnvironmentCheck(false, $"profile root is under OneDrive: {roots.UserProfile}");

        return new EnvironmentCheck(true, null);
    }

    private static bool HasOneDriveSegment(string path)
    {
        string[] parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => p.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)
                              || p.StartsWith("OneDrive ", StringComparison.OrdinalIgnoreCase)
                              || p.StartsWith("OneDrive-", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsContained(string root, string candidate)
    {
        string rootFull = NormalizePath(root) + Path.DirectorySeparatorChar;
        string candidateFull = NormalizePath(candidate) + Path.DirectorySeparatorChar;
        return candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string? TryResolve(
        RecipePathResolver resolver,
        KnownFolder knownFolder,
        string relativePath,
        out string? error)
    {
        try
        {
            error = null;
            return resolver.Resolve(knownFolder, relativePath);
        }
        catch (RecipePathException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static bool NoSiblingMatches(string path, string suffixPattern)
    {
        string? dir = Path.GetDirectoryName(path);
        string leaf = Path.GetFileName(path);
        return string.IsNullOrEmpty(dir)
               || !Directory.Exists(dir)
               || !Directory.EnumerateFiles(dir, leaf + suffixPattern, SearchOption.TopDirectoryOnly).Any();
    }

    private static GitReadBack ReadGitEmail()
    {
        foreach (string exe in GitCandidates())
        {
            try
            {
                using var proc = new Process();
                proc.StartInfo = new ProcessStartInfo(exe, "config --global user.email")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                proc.Start();
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(5000);
                return new GitReadBack(proc.ExitCode, stdout.Trim(), stderr.Trim(), exe);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                if (exe == GitCandidates().Last())
                    return new GitReadBack(-1, null, ex.Message, exe);
            }
        }

        return new GitReadBack(-1, null, "git executable not found", null);
    }

    private static IReadOnlyList<string> GitCandidates()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return new[]
        {
            "git",
            Path.Combine(programFiles, "Git", "cmd", "git.exe"),
        };
    }

    private static List<PruneProofEntry> BuildPruneProof(string packageDir, IReadOnlyList<string> leaves)
    {
        var proof = new List<PruneProofEntry>();
        foreach (string leaf in leaves)
        {
            string[] hits = Directory.Exists(packageDir)
                ? Directory.EnumerateFiles(packageDir, leaf, SearchOption.AllDirectories).ToArray()
                : Array.Empty<string>();
            proof.Add(new PruneProofEntry(leaf, hits));
        }

        return proof;
    }

    private static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteEvidence<T>(string outputDir, Mode mode, T details, bool pass)
    {
        Directory.CreateDirectory(outputDir);
        var envelope = new EvidenceEnvelope<T>(
            pass,
            mode.ToString().ToLowerInvariant(),
            DateTime.UtcNow,
            Environment.MachineName,
            ProfileRoots.ForCurrentUser().UserProfile,
            details);
        string path = Path.Combine(outputDir, $"realmig-{mode.ToString().ToLowerInvariant()}-evidence.json");
        File.WriteAllText(path, JsonSerializer.Serialize(envelope, JsonOptions));
    }

    private static TargetEntry ToTargetEntry(MigrationRestoreTarget target) =>
        new(
            target.RecipeId,
            target.EntryId,
            target.KnownFolder.ToString(),
            target.RelativePath,
            target.PackageRelativeSource,
            target.PortabilityClass.ToString(),
            target.RestoreTier.ToString(),
            target.Sha256);

    private static SkipEntry ToSkipEntry(RecipeItemSkip skip) => new(skip.ItemPath, skip.Reason);

    private static RestoreSkipEntry ToRestoreSkipEntry(RestoreSkip skip) =>
        new(skip.Target.RecipeId, skip.Target.EntryId, skip.Target.RelativePath, skip.Reason.ToString(), skip.Note);

    private static ActionEntry ToActionEntry(ActionResult result) =>
        new(result.ActionId, result.Kind, result.Status.ToString(), result.Detail);

    private static RejectedUndoEntry ToRejectedUndoEntry(RejectedRestoreUndoStep rejected) =>
        new(rejected.Step.EntryId, rejected.Step.TargetPath, rejected.Step.BakPath, rejected.Reason);

    private static RestoreReportEvidence ToReportEvidence(RestoreReport report) =>
        new(
            report.Restored.Count,
            report.ReinstallEnqueued.Count,
            report.Manual.Count,
            report.Restored.Select(ToReportEntry).ToArray(),
            report.ReinstallEnqueued.Select(ToReportEntry).ToArray(),
            report.Manual.Select(ToReportEntry).ToArray());

    private static ReportEntry ToReportEntry(RestoreReportEntry entry) =>
        new(entry.Id, entry.RecipeId, entry.Disposition.ToString(), entry.Reason, entry.Note);

    private static bool TryParseArgs(string[] args, out Config cfg, out string error)
    {
        cfg = default!;
        error = string.Empty;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mode",
            "require-guest-nonce",
            "expect-profile-root",
            "output",
            "package",
            "state",
            "recipes",
            "assert-pruned",
            "expect-git-email",
            "baseline",
        };

        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unexpected argument: {args[i]}";
                return false;
            }

            string key = args[i][2..];
            if (!allowed.Contains(key))
            {
                error = $"unknown argument: --{key}";
                return false;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"missing value for --{key}";
                return false;
            }

            values[key] = args[++i];
        }

        if (!Required(values, "mode", out string modeText, out error)
            || !Required(values, "require-guest-nonce", out string nonce, out error)
            || !Required(values, "expect-profile-root", out string expectProfile, out error)
            || !Required(values, "output", out string output, out error))
        {
            return false;
        }

        if (!Enum.TryParse(modeText, ignoreCase: true, out Mode mode))
        {
            error = $"unknown mode: {modeText}";
            return false;
        }

        if (!Guid.TryParse(nonce, out _))
        {
            error = "--require-guest-nonce must be a GUID";
            return false;
        }

        bool needPackage = mode is Mode.Backup or Mode.Fingerprint or Mode.Restore or Mode.Verify;
        bool needState = mode is Mode.Restore or Mode.Verify or Mode.Undo;
        if (needPackage && !Required(values, "package", out _, out error))
            return false;
        if (needState && !Required(values, "state", out _, out error))
            return false;
        if (mode == Mode.Backup && !Required(values, "recipes", out _, out error))
            return false;
        if (mode == Mode.Verify
            && (!Required(values, "expect-git-email", out _, out error)
                || !Required(values, "baseline", out _, out error)))
        {
            return false;
        }

        cfg = new Config(
            mode,
            nonce,
            expectProfile,
            output,
            values.GetValueOrDefault("package"),
            values.GetValueOrDefault("state"),
            SplitCsv(values.GetValueOrDefault("recipes")),
            SplitCsv(values.GetValueOrDefault("assert-pruned")),
            values.GetValueOrDefault("expect-git-email"),
            values.GetValueOrDefault("baseline"));
        return true;
    }

    private static bool Required(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value,
        out string error)
    {
        if (values.TryGetValue(key, out value!) && !string.IsNullOrWhiteSpace(value))
        {
            error = string.Empty;
            return true;
        }

        error = $"missing required argument: --{key}";
        value = string.Empty;
        return false;
    }

    private static IReadOnlyList<string> SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            Usage:
              MigrationRealRestore --mode <backup|fingerprint|restore|verify|undo>
                --require-guest-nonce <guid> --expect-profile-root <path> --output <dir>
                [--package <dir>] [--state <dir>] [--recipes <csv>]
                [--assert-pruned <csv>] [--expect-git-email <value>] [--baseline <json>]
            """);
    }
}

internal enum Mode
{
    Backup,
    Fingerprint,
    Restore,
    Verify,
    Undo,
}

internal sealed record Config(
    Mode Mode,
    string RequiredGuestNonce,
    string ExpectProfileRoot,
    string OutputDir,
    string? PackageDir,
    string? StateDir,
    IReadOnlyList<string> Recipes,
    IReadOnlyList<string> AssertPruned,
    string? ExpectGitEmail,
    string? BaselinePath);

internal sealed record NonceCheck(bool Pass, string? Reason);
internal sealed record EnvironmentCheck(bool Pass, string? Reason);

internal sealed class UsageException(string message) : Exception(message);

internal sealed record EvidenceEnvelope<T>(
    bool Pass,
    string Mode,
    DateTime GeneratedAt,
    string MachineName,
    string ProfileRoot,
    T Details);

internal sealed record FailureEvidence(string ErrorType, string Message);

internal sealed record BackupEvidence(
    bool Authorized,
    int PlanActionCount,
    IReadOnlyList<SkipEntry> PlanSkips,
    IReadOnlyList<SkipEntry> RunSkips,
    IReadOnlyList<SkipEntry> FinalizationSkips,
    IReadOnlyList<TargetEntry> Targets,
    IReadOnlyList<PruneProofEntry> PruneProof,
    BackupGates Gates);

internal sealed record BackupGates(bool HasRestorableTargets, bool HasChromeTarget, bool NoPruneLeaks);

internal sealed record TargetEntry(
    string RecipeId,
    string EntryId,
    string KnownFolder,
    string RelativePath,
    string PackageRelativeSource,
    string PortabilityClass,
    string RestoreTier,
    string Sha256);

internal sealed record SkipEntry(string ItemPath, string Reason);
internal sealed record PruneProofEntry(string FileName, IReadOnlyList<string> Hits);

internal sealed record FingerprintEvidence(
    IReadOnlyList<FingerprintEntry> Entries,
    GitReadBack GitEmailBefore);

internal sealed record FingerprintEntry(
    string RecipeId,
    string EntryId,
    string RelativePath,
    string? DestPath,
    string? ResolveError,
    bool Exists,
    string? Sha256);

internal sealed record GitReadBack(int ExitCode, string? StdOut, string? StdErr, string? Executable);

internal sealed record RestoreEvidence(
    bool Authorized,
    bool ExecutionAuthorized,
    string PreviewPlanHash,
    string ExecutionPlanHash,
    int PlanActionCount,
    IReadOnlyList<RestoreSkipEntry> PlanSkips,
    IReadOnlyList<ActionEntry> Actions,
    RestoreReportEvidence RestoreReport,
    RestoreGates Gates);

internal sealed record RestoreGates(
    bool AllChromeManualMachineLocked,
    bool NoChromeRestored,
    bool EnoughRestored,
    bool NoFailedOrBlockedActions);

internal sealed record RestoreSkipEntry(
    string RecipeId,
    string EntryId,
    string RelativePath,
    string Reason,
    string Note);

internal sealed record ActionEntry(string ActionId, string Kind, string Status, string Detail);

internal sealed record RestoreReportEvidence(
    int RestoredCount,
    int ReinstallEnqueuedCount,
    int ManualCount,
    IReadOnlyList<ReportEntry> Restored,
    IReadOnlyList<ReportEntry> ReinstallEnqueued,
    IReadOnlyList<ReportEntry> Manual);

internal sealed record ReportEntry(string Id, string RecipeId, string Disposition, string Reason, string Note);

internal sealed record VerifyEvidence(
    IReadOnlyList<VerifyRestoredEntry> RestoredEntries,
    GitVerifyEvidence GitReadBack,
    IReadOnlyList<MachineLockedCheck> MachineLockedChecks,
    IReadOnlyList<AppConsumptionEntry> AppConsumption,
    VerifyGates Gates);

internal sealed record VerifyGates(bool RestoredShaMatch, bool GitReadBackMatches, bool MachineLockedUntouched);

internal sealed record VerifyRestoredEntry(
    string RecipeId,
    string EntryId,
    string RelativePath,
    string? DestPath,
    string? ResolveError,
    bool DestExists,
    string ManifestSha,
    string? ActualSha,
    bool ShaMatch);

internal sealed record GitVerifyEvidence(
    string? Expected,
    string? Actual,
    int ExitCode,
    string? Executable,
    bool Pass);

internal sealed record MachineLockedCheck(
    string RecipeId,
    string EntryId,
    string RelativePath,
    string? DestPath,
    string? ResolveError,
    bool? BaselineExists,
    string? BaselineSha,
    bool CurrentExists,
    string? CurrentSha,
    bool SameAsBaseline,
    bool NoBakSibling,
    bool NoTmpSibling,
    bool Pass);

internal sealed record AppConsumptionEntry(
    string RecipeId,
    string? Executable,
    bool Launched,
    string? Error,
    IReadOnlyList<AppFileProbe> Files);

internal sealed record AppFileProbe(
    string EntryId,
    string Path,
    string? ShaBefore,
    string? ShaAfter,
    bool? ResetByApp);

internal sealed record UndoEvidence(
    bool Authorized,
    bool ExecutionAuthorized,
    string PreviewPlanHash,
    string ExecutionPlanHash,
    int PlanActionCount,
    IReadOnlyList<RejectedUndoEntry> RejectedSteps,
    IReadOnlyList<ActionEntry> Actions,
    IReadOnlyList<UndoEntryEvidence> Entries,
    GitReadBackEvidence GitEmailAfterUndo,
    UndoGates Gates);

internal sealed record UndoGates(bool BakEntriesReverted, bool CreatedEntriesRejectedAndStillExist, bool NoFailedOrBlockedActions);

internal sealed record RejectedUndoEntry(string EntryId, string TargetPath, string? BakPath, string Reason);

internal sealed record UndoEntryEvidence(
    string EntryId,
    string TargetPath,
    string? BakPath,
    string? ShaBefore,
    string? RevertedSha,
    bool TargetExistsAfterUndo,
    bool? RevertedMatchesBefore,
    string? RejectedReason,
    bool? RejectedReasonContainsCreated);

internal sealed record GitReadBackEvidence(string? Actual, int ExitCode, string? Executable);

internal sealed class ThrowingFileDeleteAdapter : IFileDeleteAdapter
{
    public void Delete(FileDeleteAction action) =>
        throw new InvalidOperationException("file delete not expected in MigrationRealRestore");
}

internal sealed class ThrowingRegistryAdapter : IRegistryAdapter
{
    public void Delete(RegistryDeleteAction action) =>
        throw new InvalidOperationException("registry delete not expected in MigrationRealRestore");
}

internal sealed class ThrowingServiceAdapter : IServiceAdapter
{
    public void Apply(ServiceDeleteAction action) =>
        throw new InvalidOperationException("service op not expected in MigrationRealRestore");
}

internal sealed class ThrowingTaskAdapter : ITaskAdapter
{
    public void Apply(TaskDeleteAction action) =>
        throw new InvalidOperationException("task op not expected in MigrationRealRestore");
}

internal sealed class ThrowingProcessAdapter : IProcessAdapter
{
    public void Run(CommandAction action) =>
        throw new InvalidOperationException("process run not expected in MigrationRealRestore");
}

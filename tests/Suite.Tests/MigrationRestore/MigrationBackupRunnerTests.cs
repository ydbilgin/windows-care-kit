using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests.MigrationRestore;

/// <summary>
/// The PRODUCTION <see cref="MigrationBackupRunner"/> — the orchestration that was previously only in a test
/// helper. These run host-safe over real temp dirs through the production components (RecipeResolver over the
/// real Win32 FS, the gated executor seam, real hasher + store). They prove: the index bug is structurally
/// gone, a full A→package→relocated-B round-trip works through production code, the recipe-id grammar + the
/// duplicate/refusal guards hold, and routing the copy through the gate does NOT block a legitimate temp/export
/// package directory.
/// </summary>
public class MigrationBackupRunnerTests
{
    private static readonly DateTime T0 = new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

    private static MigrationBackupRunner Runner(ProfileRoots roots, SafetyGate gate)
        => new(
            new RecipeResolver(new RecipePathResolver(roots), new Win32RecipeFileSystem()),
            new BackupExecutorAdapter(MigrationRestoreTestData.Executor(gate)),
            new Sha256Hasher(),
            new PhysicalFileSystem(),
            new MigrationRestoreManifestStore(),
            gate);

    private static MigrationBackupRunResult RunBackup(
        ProfileRoots roots, string packageDir, params MigrationRecipe[] recipes)
    {
        SafetyGate gate = MigrationRestoreTestData.GateAllowingPackage(packageDir);
        MigrationBackupRunner runner = Runner(roots, gate);
        MigrationBackupPlanResult plan = runner.BuildPlan(recipes, packageDir, T0);
        return runner.Run(plan, plan.Plan.ComputeHash(), packageDir);
    }

    private static MigrationRecipe GitRecipe() => new(
        SchemaVersion: 1, Id: "git.config", DisplayName: "Git", Category: "dev-tools",
        Detect: new RecipeDetect(KnownFolder.UserProfile, ".gitconfig", Exists: true),
        Items: new[] { new RecipeItem(".gitconfig", Array.Empty<string>(), Array.Empty<string>()) },
        Exclude: Array.Empty<string>(), SecretRule: "global",
        PortabilityClass: PortabilityClass.ProfileRelative,
        Restore: new RecipeRestore(RestoreStrategy.MergeAfterInstall, RestorePhase.ConfigWrite, Array.Empty<string>()));

    private static MigrationRecipe ClaudeSettingsRecipe() => new(
        SchemaVersion: 1, Id: "anthropic.claude-code", DisplayName: "Claude", Category: "dev-tools",
        Detect: new RecipeDetect(KnownFolder.AppData, "wckclaude/settings.json", Exists: true),
        Items: new[] { new RecipeItem("wckclaude/settings.json", Array.Empty<string>(), Array.Empty<string>()) },
        Exclude: Array.Empty<string>(), SecretRule: "global",
        PortabilityClass: PortabilityClass.ProfileRelative,
        Restore: new RecipeRestore(RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, Array.Empty<string>()));

    /// <summary>
    /// THE index-bug regression (non-vacuous): item[0] is a missing path (sandbox-SKIPPED) and item[1] is a
    /// real single file. The skip PRECEDES the passing item, so the old <c>recipe.Items[i]</c> correlation
    /// (i=0 over the single bridged item) would have recorded item[0]'s path/KnownFolder. The carried
    /// <c>RecipePath</c> records item[1]'s — proven here.
    /// </summary>
    [Fact]
    public void Manifest_target_tracks_the_passing_item_not_the_skipped_one_at_index_0()
    {
        string root = MigrationRestoreTestData.TempDir("idxbug");
        try
        {
            string profile = Path.Combine(root, "Users", "alice");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, ".gitconfig"), "[user]\n name = alice");
            // NOTE: no "missing.cfg" on disk — item[0] is sandbox-skipped (source does not exist).

            var roots = new ProfileRoots(profile, Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "Local"));

            // detect on .gitconfig (exists) → recipe applies; item[0] missing → skipped; item[1] real → bridged.
            var recipe = new MigrationRecipe(
                SchemaVersion: 1, Id: "git.config", DisplayName: "Git", Category: "dev-tools",
                Detect: new RecipeDetect(KnownFolder.UserProfile, ".gitconfig", Exists: true),
                Items: new[]
                {
                    new RecipeItem("missing.cfg", Array.Empty<string>(), Array.Empty<string>()),  // index 0 → skipped
                    new RecipeItem(".gitconfig", Array.Empty<string>(), Array.Empty<string>()),   // index 1 → real
                },
                Exclude: Array.Empty<string>(), SecretRule: "global",
                PortabilityClass: PortabilityClass.ProfileRelative,
                Restore: new RecipeRestore(RestoreStrategy.MergeAfterInstall, RestorePhase.ConfigWrite, Array.Empty<string>()));

            string pkg = Path.Combine(root, "package");
            MigrationBackupRunResult result = RunBackup(roots, pkg, recipe);

            Assert.True(result.Authorized);
            MigrationRestoreTarget target = Assert.Single(result.Manifest.Targets);

            // The bug would have stored "missing.cfg" (item[0]); the fix stores ".gitconfig" (item[1]).
            Assert.Equal(".gitconfig", target.RelativePath);
            Assert.Equal(KnownFolder.UserProfile, target.KnownFolder);
            Assert.NotEqual("missing.cfg", target.RelativePath);

            // The skipped item is surfaced honestly (not silently dropped).
            Assert.Contains(result.SkippedItems, s => s.ItemPath == "missing.cfg");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// E2E through the PRODUCTION runner: Profile A (alice) .gitconfig + AppData settings.json → BuildPlan+Run →
    /// manifest; then the unchanged <see cref="MigrationRestoreRunner"/> over a fabricated, relocated Profile B
    /// (different username + AppData on a different sub-path) → restore. Every file lands at B's CORRECT
    /// KnownFolder (NOT A's), and the restored bytes' SHA equals the manifest SHA.
    /// </summary>
    [Fact]
    public void E2E_backup_then_restore_places_files_at_profile_B_and_sha_matches_the_manifest()
    {
        string root = MigrationRestoreTestData.TempDir("e2e");
        try
        {
            // --- Profile A (old machine): username "alice". ---
            string aProfile = Path.Combine(root, "A", "Users", "alice");
            string aAppData = Path.Combine(aProfile, "AppData", "Roaming");
            Directory.CreateDirectory(aProfile);
            Directory.CreateDirectory(Path.Combine(aAppData, "wckclaude"));
            File.WriteAllText(Path.Combine(aProfile, ".gitconfig"), "[user]\n name = alice");
            File.WriteAllText(Path.Combine(aAppData, "wckclaude", "settings.json"), "{\"theme\":\"dark\"}");
            var aRoots = new ProfileRoots(aProfile, aAppData, Path.Combine(aProfile, "AppData", "Local"));

            // --- Backup A → package + manifest, through the production runner. ---
            string pkg = Path.Combine(root, "package");
            MigrationBackupRunResult backup = RunBackup(aRoots, pkg, GitRecipe(), ClaudeSettingsRecipe());
            Assert.True(backup.Authorized);
            Assert.Equal(2, backup.Manifest.Targets.Count);
            Assert.Empty(backup.FinalizationSkips);

            // --- Profile B (new machine): DIFFERENT username "bob", RELOCATED AppData outside the profile. ---
            string bProfile = Path.Combine(root, "B", "Users", "bob");
            string bAppData = Path.Combine(root, "B", "RelocatedAppData", "bob", "Roaming");
            Directory.CreateDirectory(bProfile);
            var bRoots = new ProfileRoots(bProfile, bAppData, Path.Combine(root, "B", "RelocatedAppData", "bob", "Local"));

            string bRootDir = Path.Combine(root, "B");
            SafetyGate gate = MigrationRestoreTestData.GateForProfile(bRootDir, Path.Combine(bRootDir, "Users"));
            GatedExecutor executor = MigrationRestoreTestData.Executor(gate);

            MigrationRestoreManifest manifest = new MigrationRestoreManifestStore().Load(pkg);
            var restore = new MigrationRestoreRunner(new RecipePathResolver(bRoots), gate);
            MigrationRestorePlanResult plan = restore.BuildPlan(manifest, pkg, RestoreState.Empty, T0);
            Assert.Equal(2, plan.Plan.Actions.Count);
            Assert.Empty(plan.Skipped);

            ExecutionReport report = executor.ExecuteWithReport(plan.Plan, plan.Plan.ComputeHash());
            Assert.True(report.Authorized, string.Join(",", report.Results.Select(r => r.Detail)));
            Assert.True(report.Results.All(r => r.Status == ActionStatus.Done),
                string.Join(",", report.Results.Select(r => $"{r.Kind}:{r.Status}:{r.Detail}")));

            // --- Files at B's CORRECT KnownFolder, NOT A's. ---
            string bGit = Path.Combine(bProfile, ".gitconfig");
            string bSettings = Path.Combine(bAppData, "wckclaude", "settings.json");
            Assert.True(File.Exists(bGit), "git config must land under B's USERPROFILE");
            Assert.True(File.Exists(bSettings), "settings.json must land under B's relocated APPDATA");
            Assert.NotEqual(Path.Combine(aProfile, ".gitconfig"), bGit);
            Assert.NotEqual(Path.Combine(aAppData, "wckclaude", "settings.json"), bSettings);

            // --- The restored bytes' SHA equals the manifest's recorded SHA (provenance + integrity). ---
            var hasher = new Sha256Hasher();
            foreach (MigrationRestoreTarget t in manifest.Targets)
            {
                string dest = new RecipePathResolver(bRoots).Resolve(t.KnownFolder, t.RelativePath);
                Assert.Equal(t.Sha256, hasher.ComputeFileSha256(dest));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// INSTALL-phase enablement: a MULTI-ITEM recipe carrying a v2 install block emits EXACTLY ONE install entry
    /// (per-recipe, NOT per restore target) in <c>migration-install.json</c>, while the restore manifest still
    /// carries N config targets (one per backed-up single file). The strict store re-loads the install manifest
    /// and the EXISTING gated <see cref="InstallPlanner"/> turns it into the exact, exported command action.
    /// </summary>
    [Fact]
    public void Multi_item_recipe_with_install_emits_exactly_one_install_entry_plus_N_config_targets()
    {
        string root = MigrationRestoreTestData.TempDir("install-multi");
        try
        {
            string profile = Path.Combine(root, "Users", "alice");
            Directory.CreateDirectory(Path.Combine(profile, ".app"));
            File.WriteAllText(Path.Combine(profile, ".app", "settings.json"), "{\"a\":1}");
            File.WriteAllText(Path.Combine(profile, ".app", "keymap.json"), "{\"b\":2}");
            var roots = new ProfileRoots(profile, Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "Local"));

            // ONE recipe, TWO single-file items, plus an install block (winget). Detect on .app (exists).
            var recipe = new MigrationRecipe(
                SchemaVersion: 2, Id: "vendor.app", DisplayName: "Vendor App", Category: "dev-tools",
                Detect: new RecipeDetect(KnownFolder.UserProfile, ".app", Exists: true),
                Items: new[]
                {
                    new RecipeItem(".app/settings.json", Array.Empty<string>(), Array.Empty<string>()),
                    new RecipeItem(".app/keymap.json", Array.Empty<string>(), Array.Empty<string>()),
                },
                Exclude: Array.Empty<string>(), SecretRule: "global",
                PortabilityClass: PortabilityClass.ProfileRelative,
                Restore: new RecipeRestore(RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, Array.Empty<string>()))
            {
                Install = new RecipeInstall(RecipeInstallMethod.Winget, "Vendor.App", null, null, RequiresAdmin: false, RebootExpected: false),
            };

            string pkg = Path.Combine(root, "package");
            MigrationBackupRunResult result = RunBackup(roots, pkg, recipe);

            Assert.True(result.Authorized);
            Assert.Equal(2, result.Manifest.Targets.Count); // N config targets (both single files)

            // EXACTLY ONE install entry, re-loaded through the STRICT store (not the permissive loader).
            InstallManifest install = new MigrationInstallManifestStore().Load(pkg);
            InstallEntry e = Assert.Single(install.Entries);
            Assert.Equal("migration:vendor.app:install", e.Id);
            Assert.Equal(InstallMethod.Winget, e.Method);
            Assert.Equal("Vendor.App", e.WingetId);

            // The package self-describes the reinstall: feed it to the EXISTING gated planner.
            var planner = new InstallPlanner(MigrationRestoreTestData.GateAllowingPackage(pkg), new FakeDriverGuard());
            InstallPlanResult plan = planner.BuildPlan(install, RestoreState.Empty, T0);
            CommandAction cmd = Assert.IsType<CommandAction>(Assert.Single(plan.Plan.Actions));
            Assert.Equal("Vendor.App", cmd.Arguments[2]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>An authorized backup with NO install-block recipes still writes a VALID EMPTY install manifest.</summary>
    [Fact]
    public void Authorized_run_with_no_install_writes_a_valid_empty_install_manifest()
    {
        string root = MigrationRestoreTestData.TempDir("install-empty");
        try
        {
            string profile = Path.Combine(root, "Users", "alice");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, ".gitconfig"), "[user]\n name = alice");
            var roots = new ProfileRoots(profile, Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "Local"));

            string pkg = Path.Combine(root, "package");
            MigrationBackupRunResult result = RunBackup(roots, pkg, GitRecipe()); // GitRecipe is v1, no install block
            Assert.True(result.Authorized);

            Assert.True(File.Exists(new MigrationInstallManifestStore().PathFor(pkg)), "an authorized run writes an install manifest");
            InstallManifest install = new MigrationInstallManifestStore().Load(pkg);
            Assert.Empty(install.Entries);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A REFUSED run writes NEITHER manifest (critic fix #4: install save sits after the refusal return).</summary>
    [Fact]
    public void Refused_run_writes_no_install_manifest_either()
    {
        string root = MigrationRestoreTestData.TempDir("install-refused");
        try
        {
            string profile = Path.Combine(root, "Users", "alice");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, ".gitconfig"), "[user]\n name = alice");
            var roots = new ProfileRoots(profile, Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "Local"));

            string pkg = @"C:\Program Files\wck-should-never-write";
            SafetyGate gate = new(ProtectedResources.ForCurrentSystem(), new Win32PathCanonicalizer());
            MigrationBackupRunner runner = Runner(roots, gate);

            var recipe = new MigrationRecipe(
                SchemaVersion: 2, Id: "git.config", DisplayName: "Git", Category: "dev-tools",
                Detect: new RecipeDetect(KnownFolder.UserProfile, ".gitconfig", Exists: true),
                Items: new[] { new RecipeItem(".gitconfig", Array.Empty<string>(), Array.Empty<string>()) },
                Exclude: Array.Empty<string>(), SecretRule: "global",
                PortabilityClass: PortabilityClass.ProfileRelative,
                Restore: new RecipeRestore(RestoreStrategy.MergeAfterInstall, RestorePhase.ConfigWrite, Array.Empty<string>()))
            {
                Install = new RecipeInstall(RecipeInstallMethod.Winget, "Git.Git", null, null, false, false),
            };

            MigrationBackupPlanResult plan = runner.BuildPlan(new[] { recipe }, pkg, T0);
            MigrationBackupRunResult result = runner.Run(plan, plan.Plan.ComputeHash(), pkg);

            Assert.False(result.Authorized);
            Assert.False(File.Exists(new MigrationInstallManifestStore().PathFor(pkg)), "a refused run must write NO install manifest");
            Assert.False(File.Exists(new MigrationRestoreManifestStore().PathFor(pkg)), "a refused run must write NO restore manifest");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>(c) A duplicate recipe id in the supplied set is skipped + surfaced — no silent overwrite.</summary>
    [Fact]
    public void Duplicate_recipe_id_is_skipped_and_surfaced_no_overwrite()
    {
        string root = MigrationRestoreTestData.TempDir("dup");
        try
        {
            string profile = Path.Combine(root, "Users", "alice");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, ".gitconfig"), "[user]\n name = alice");
            var roots = new ProfileRoots(profile, Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "Local"));

            string pkg = Path.Combine(root, "package");
            // Two recipes with the SAME id → the second must be rejected, leaving exactly one backed-up target.
            MigrationBackupRunResult result = RunBackup(roots, pkg, GitRecipe(), GitRecipe());

            Assert.True(result.Authorized);
            Assert.Single(result.Manifest.Targets);
            Assert.Contains(result.SkippedItems, s => s.Reason.Contains("duplicate recipe id"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// (d) A REFUSED run — the package dir is under a write-protected root (Program Files). The whole plan is
    /// refused (all-or-nothing): Authorized=false, NO manifest written, nothing copied.
    /// </summary>
    [Fact]
    public void Refused_run_writes_no_manifest_and_copies_nothing()
    {
        string root = MigrationRestoreTestData.TempDir("refused");
        try
        {
            string profile = Path.Combine(root, "Users", "alice");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, ".gitconfig"), "[user]\n name = alice");
            var roots = new ProfileRoots(profile, Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "Local"));

            // A package dir under a write-protected system root: the gate refuses the copy destination, so the
            // GatedExecutor refuses to authorize the WHOLE plan. We never write here (it would be blocked anyway).
            string pkg = @"C:\Program Files\wck-should-never-write";

            // The gate is the production system policy (Program Files is write-protected).
            SafetyGate gate = new(ProtectedResources.ForCurrentSystem(), new Win32PathCanonicalizer());
            MigrationBackupRunner runner = Runner(roots, gate);
            MigrationBackupPlanResult plan = runner.BuildPlan(new[] { GitRecipe() }, pkg, T0);
            MigrationBackupRunResult result = runner.Run(plan, plan.Plan.ComputeHash(), pkg);

            Assert.False(result.Authorized);
            Assert.Empty(result.Manifest.Targets);
            Assert.False(File.Exists(new MigrationRestoreManifestStore().PathFor(pkg)),
                "a refused run must write NO manifest");
            Assert.False(Directory.Exists(pkg), "a refused run must not create the protected package dir");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// Gate-allows-external: a BuildPlan whose packageDir is a non-protected local/temp path produces a plan
    /// whose copy actions the gate AUTHORIZES — proving routing through the gate does not block legitimate
    /// exports (the decision's refutation of the "gate blocks external dirs" fear).
    /// </summary>
    [Fact]
    public void Gate_authorizes_copy_into_a_non_protected_local_package_dir()
    {
        string root = MigrationRestoreTestData.TempDir("gate-ext");
        try
        {
            string profile = Path.Combine(root, "Users", "alice");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, ".gitconfig"), "[user]\n name = alice");
            var roots = new ProfileRoots(profile, Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "Local"));

            string pkg = Path.Combine(root, "external-export");
            SafetyGate gate = MigrationRestoreTestData.GateAllowingPackage(pkg);
            MigrationBackupRunner runner = Runner(roots, gate);
            MigrationBackupPlanResult plan = runner.BuildPlan(new[] { GitRecipe() }, pkg, T0);

            CopyAction copy = Assert.IsType<CopyAction>(Assert.Single(plan.Plan.Actions));
            Assert.True(gate.Evaluate(copy).Allowed, gate.Evaluate(copy).Reason);

            // And the run actually authorizes + writes the manifest.
            MigrationBackupRunResult result = runner.Run(plan, plan.Plan.ComputeHash(), pkg);
            Assert.True(result.Authorized);
            Assert.True(File.Exists(new MigrationRestoreManifestStore().PathFor(pkg)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// Pins the manifest RE-GATE branch (the TOCTOU defense): the EXECUTOR authorizes + runs the copies via a real
    /// gate that allows the package dir, but the runner's manifest re-gate is fed a gate that blocks ONLY the
    /// package-root manifest-write probe — simulating the package root being reparse-swapped between the copies and
    /// the manifest write. The copies land (Authorized stays true), but NEITHER manifest is written and the block
    /// is surfaced as a finalization-skip rather than thrown (a successful backup must not crash on the race).
    /// </summary>
    [Fact]
    public void Regate_block_before_manifest_write_writes_neither_manifest_and_surfaces_it()
    {
        string root = MigrationRestoreTestData.TempDir("regate-block");
        try
        {
            string profile = Path.Combine(root, "Users", "alice");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, ".gitconfig"), "[user]\n name = alice");
            var roots = new ProfileRoots(profile, Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "Local"));

            string pkg = Path.Combine(root, "package");
            SafetyGate realGate = MigrationRestoreTestData.GateAllowingPackage(pkg); // executor authorizes the copies
            var regate = new ManifestWriteBlockingGate(realGate);                    // runner re-gate blocks the manifest probe

            var runner = new MigrationBackupRunner(
                new RecipeResolver(new RecipePathResolver(roots), new Win32RecipeFileSystem()),
                new BackupExecutorAdapter(MigrationRestoreTestData.Executor(realGate)),
                new Sha256Hasher(),
                new PhysicalFileSystem(),
                new MigrationRestoreManifestStore(),
                regate);

            MigrationBackupPlanResult plan = runner.BuildPlan(new[] { GitRecipe() }, pkg, T0);
            MigrationBackupRunResult result = runner.Run(plan, plan.Plan.ComputeHash(), pkg);

            // Copies were authorized + ran; only the manifest write was blocked.
            Assert.True(result.Authorized);
            Assert.Empty(result.Manifest.Targets);
            Assert.False(File.Exists(new MigrationRestoreManifestStore().PathFor(pkg)),
                "a blocked re-gate must write NO restore manifest");
            Assert.False(File.Exists(new MigrationInstallManifestStore().PathFor(pkg)),
                "a blocked re-gate must write NO install manifest");
            Assert.Contains(result.FinalizationSkips, s => s.Reason.Contains("gate", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// A gate that delegates to a real inner gate but blocks ONLY the synthetic manifest-write probe (a
    /// <see cref="CopyAction"/> whose destination is a migration manifest at the package root) — so the plan's
    /// real copies still authorize while the post-copy re-gate fails, exercising the TOCTOU branch host-safe.
    /// </summary>
    private sealed class ManifestWriteBlockingGate : ISafetyGate
    {
        private readonly ISafetyGate _inner;
        public ManifestWriteBlockingGate(ISafetyGate inner) => _inner = inner;

        public SafetyVerdict Evaluate(PlannedAction action)
            => action is CopyAction c && IsMigrationManifest(c.Destination)
                ? SafetyVerdict.Block("simulated TOCTOU: package root reparsed before manifest write")
                : _inner.Evaluate(action);

        public PlanValidationResult Validate(OperationPlan plan) => _inner.Validate(plan);

        private static bool IsMigrationManifest(string destination)
            => destination.EndsWith(MigrationRestoreManifest.FileName, StringComparison.OrdinalIgnoreCase)
            || destination.EndsWith("migration-install.json", StringComparison.OrdinalIgnoreCase);
    }
}

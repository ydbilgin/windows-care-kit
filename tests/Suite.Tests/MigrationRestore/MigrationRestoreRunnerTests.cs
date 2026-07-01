using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests.MigrationRestore;

/// <summary>
/// Slice 2 RESTORE runner unit + execution-path proofs: the F4 machine-locked fail-safe (0 actions + 0
/// writes), the F2 allow-list, the typed-rebind rejections, and resume.
/// </summary>
public class MigrationRestoreRunnerTests
{
    private static readonly DateTime T0 = new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

    private static (string pkg, string profile, MigrationRestoreRunner runner, GatedExecutor exec, SafetyGate gate) Setup(string tag)
    {
        string root = MigrationRestoreTestData.TempDir(tag);
        string pkg = Path.Combine(root, "pkg");
        Directory.CreateDirectory(Path.Combine(pkg, "migration", "x"));
        File.WriteAllText(Path.Combine(pkg, "migration", "x", "settings.json"), "{}");

        string profile = Path.Combine(root, "Users", "bob");
        Directory.CreateDirectory(profile);

        SafetyGate gate = MigrationRestoreTestData.GateForProfile(profile, Path.Combine(root, "Users"));
        var runner = new MigrationRestoreRunner(
            new RecipePathResolver(new ProfileRoots(profile, Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "Local"))),
            gate);
        return (pkg, profile, runner, MigrationRestoreTestData.Executor(gate), gate);
    }

    private static MigrationRestoreTarget Target(
        string recipeId, PortabilityClass cls, string relativePath = "x.cfg",
        RestoreStrategy strategy = RestoreStrategy.ConfigWrite, string source = "migration/x/settings.json",
        RestoreTier tier = RestoreTier.ConfigCopy, KnownFolder knownFolder = KnownFolder.UserProfile,
        RestorePhase phase = RestorePhase.ConfigWrite, MigrationRecipeMeta? meta = null)
        => new(recipeId, recipeId + "#0", knownFolder, relativePath, source,
               strategy, phase, Array.Empty<string>(), cls, "sha")
        {
            RestoreTier = tier,
            MigrationMeta = meta,
        };

    /// <summary>F4 [CRITICAL] — a machine-locked target yields ZERO actions AND zero writes (fail-safe wired
    /// into the execution path: the executor can only run actions that are in the plan).</summary>
    [Theory]
    [InlineData(PortabilityClass.MachineLocked)]
    [InlineData(PortabilityClass.Partial)]
    public void MachineLocked_or_partial_target_produces_zero_actions_and_writes_nothing(PortabilityClass cls)
    {
        var (pkg, profile, runner, exec, _) = Setup("mlock");
        string parent = Directory.GetParent(pkg)!.FullName;
        try
        {
            // Allow-listed recipe id so the ONLY thing blocking it is the portability fail-safe (isolates F4).
            var manifest = new MigrationRestoreManifest(1, new[] { Target("git.config", cls, ".gitconfig") });
            MigrationRestorePlanResult result = runner.BuildPlan(manifest, pkg, RestoreState.Empty, T0);

            Assert.Empty(result.Plan.Actions); // 0 RestoreMergeAction
            Assert.Single(result.Skipped);
            Assert.Equal(RestoreSkipReason.MachineLocked, result.Skipped[0].Reason);

            // Drive the empty plan through the real executor: nothing is written under the profile.
            ExecutionReport report = exec.ExecuteWithReport(result.Plan, result.Plan.ComputeHash());
            Assert.True(report.Authorized);
            Assert.Empty(report.Results);
            Assert.False(File.Exists(Path.Combine(profile, ".gitconfig")), "machine-locked must write NOTHING");
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    /// <summary>Pass-with: the SAME shape but profile-relative + allow-listed DOES produce exactly one action
    /// and writes the file (the other half of the fail-without/pass-with pair).</summary>
    [Fact]
    public void ProfileRelative_allowlisted_target_produces_one_action_and_writes_the_file()
    {
        var (pkg, profile, runner, exec, _) = Setup("pass");
        string parent = Directory.GetParent(pkg)!.FullName;
        try
        {
            var manifest = new MigrationRestoreManifest(1,
                new[] { Target("git.config", PortabilityClass.ProfileRelative, ".gitconfig") });
            MigrationRestorePlanResult result = runner.BuildPlan(manifest, pkg, RestoreState.Empty, T0);

            Assert.Single(result.Plan.Actions);
            ExecutionReport report = exec.ExecuteWithReport(result.Plan, result.Plan.ComputeHash());
            Assert.True(report.Authorized);
            Assert.True(report.Results.All(r => r.Status == ActionStatus.Done));
            Assert.True(File.Exists(Path.Combine(profile, ".gitconfig")));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    /// <summary>F0 — a profile-relative inventory-only recipe is manual-listed and produces no config-copy action.</summary>
    [Fact]
    public void InventoryOnly_recipe_is_skipped_even_when_profile_relative()
    {
        var (pkg, _, runner, _, _) = Setup("tier");
        string parent = Directory.GetParent(pkg)!.FullName;
        try
        {
            var manifest = new MigrationRestoreManifest(1,
                new[] { Target("some.other.app", PortabilityClass.ProfileRelative, ".gitconfig", tier: RestoreTier.InventoryOnly) });
            MigrationRestorePlanResult result = runner.BuildPlan(manifest, pkg, RestoreState.Empty, T0);

            Assert.Empty(result.Plan.Actions);
            Assert.Equal(RestoreSkipReason.InventoryOnly, result.Skipped.Single().Reason);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    /// <summary>F4 — a non-profile root cannot self-promote to a restore write even with restoreTier=config-copy.</summary>
    [Fact]
    public void Non_profile_root_is_hard_blocked_even_when_tier_claims_config_copy()
    {
        var (pkg, _, runner, _, _) = Setup("nonprofile");
        string parent = Directory.GetParent(pkg)!.FullName;
        try
        {
            var manifest = new MigrationRestoreManifest(1,
                new[] { Target("programdata.app", PortabilityClass.ProfileRelative, "app.cfg", tier: RestoreTier.ConfigCopy, knownFolder: KnownFolder.ProgramData) });
            MigrationRestorePlanResult result = runner.BuildPlan(manifest, pkg, RestoreState.Empty, T0);

            Assert.Empty(result.Plan.Actions);
            Assert.Equal(RestoreSkipReason.NonProfileRoot, result.Skipped.Single().Reason);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    /// <summary>Back-compat — old manifests without restoreTier keep the old two-id allow-list behavior.</summary>
    [Fact]
    public void Legacy_unspecified_tier_uses_the_old_allow_list_only_for_compatibility()
    {
        var (pkg, _, runner, _, _) = Setup("legacy-tier");
        string parent = Directory.GetParent(pkg)!.FullName;
        try
        {
            var allowed = new MigrationRestoreTarget("git.config", "git.config#0", KnownFolder.UserProfile,
                ".gitconfig", "migration/x/settings.json", RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite,
                Array.Empty<string>(), PortabilityClass.ProfileRelative, "sha");
            var blocked = new MigrationRestoreTarget("some.other.app", "some.other.app#0", KnownFolder.UserProfile,
                "other.cfg", "migration/x/settings.json", RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite,
                Array.Empty<string>(), PortabilityClass.ProfileRelative, "sha");
            var manifest = new MigrationRestoreManifest(1, new[] { allowed, blocked });

            MigrationRestorePlanResult result = runner.BuildPlan(manifest, pkg, RestoreState.Empty, T0);

            Assert.Single(result.Plan.Actions);
            Assert.Equal(RestoreSkipReason.NotAllowListed, result.Skipped.Single().Reason);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    /// <summary>Typed rebind rejects an absolute / traversal / unknown-token destination BEFORE the gate.</summary>
    [Theory]
    [InlineData(@"C:\Windows\evil.cfg")]      // absolute / rooted
    [InlineData("../../../escape.cfg")]        // traversal
    [InlineData("%APPDATA%/x.cfg")]            // env token
    public void Rebind_rejects_unsafe_destinations(string relativePath)
    {
        var (pkg, _, runner, _, _) = Setup("rebind");
        string parent = Directory.GetParent(pkg)!.FullName;
        try
        {
            var manifest = new MigrationRestoreManifest(1,
                new[] { Target("git.config", PortabilityClass.ProfileRelative, relativePath) });
            MigrationRestorePlanResult result = runner.BuildPlan(manifest, pkg, RestoreState.Empty, T0);

            Assert.Empty(result.Plan.Actions);
            Assert.Equal(RestoreSkipReason.RebindRejected, result.Skipped.Single().Reason);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void Package_relative_source_that_escapes_package_is_rejected()
    {
        var (pkg, _, runner, _, _) = Setup("source-escape");
        string parent = Directory.GetParent(pkg)!.FullName;
        try
        {
            File.WriteAllText(Path.Combine(parent, "escape.txt"), "{}");
            var manifest = new MigrationRestoreManifest(1,
                new[] { Target("git.config", PortabilityClass.ProfileRelative, ".gitconfig", source: @"..\escape.txt") });

            MigrationRestorePlanResult result = runner.BuildPlan(manifest, pkg, RestoreState.Empty, T0);

            Assert.Empty(result.Plan.Actions);
            RestoreSkip skip = Assert.Single(result.Skipped);
            Assert.Equal(RestoreSkipReason.PackageSourceRejected, skip.Reason);
            Assert.Contains("outside", skip.Note, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    /// <summary>Resume — a target already Done in the checkpoint is skipped (not re-planned).</summary>
    [Fact]
    public void Already_done_target_is_skipped_on_resume()
    {
        var (pkg, _, runner, _, _) = Setup("resume");
        string parent = Directory.GetParent(pkg)!.FullName;
        try
        {
            MigrationRestoreTarget t = Target("git.config", PortabilityClass.ProfileRelative, ".gitconfig");
            var manifest = new MigrationRestoreManifest(1, new[] { t });
            RestoreState state = RestoreState.Empty.With(t.EntryId, RestoreEntryStatus.Done, T0);

            MigrationRestorePlanResult result = runner.BuildPlan(manifest, pkg, state, T0);
            Assert.Empty(result.Plan.Actions);
            Assert.Equal(RestoreSkipReason.AlreadyDone, result.Skipped.Single().Reason);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    /// <summary>F1/F2 — migration restore can prepend gated reinstall actions from the existing install planner.</summary>
    [Fact]
    public void Install_manifest_queues_reinstall_action_before_config_restore()
    {
        var (pkg, _, runner, _, gate) = Setup("reinstall");
        string parent = Directory.GetParent(pkg)!.FullName;
        try
        {
            var manifest = new MigrationRestoreManifest(1,
                new[] { Target("git.config", PortabilityClass.ProfileRelative, ".gitconfig", tier: RestoreTier.MergeAfterInstall) });
            var install = new InstallManifest(new[]
            {
                new InstallEntry(
                    Id: "migration:git.config:install",
                    Phase: "install",
                    Category: "dev-tools",
                    Method: InstallMethod.Winget,
                    WingetId: "Git.Git",
                    NpmPackage: null,
                    RequiresAdmin: true,
                    RebootExpected: false,
                    RestoreOrder: 0,
                    Description: "Git"),
            });

            MigrationRestorePlanResult result = runner.BuildPlan(
                manifest, pkg, RestoreState.Empty, T0, install, new InstallPlanner(gate, new FakeDriverGuard()));

            Assert.Equal(2, result.Plan.Actions.Count);
            CommandAction cmd = Assert.IsType<CommandAction>(result.Plan.Actions[0]);
            RestoreMergeAction merge = Assert.IsType<RestoreMergeAction>(result.Plan.Actions[1]);
            Assert.Equal("Git.Git", cmd.Arguments[2]);
            Assert.EndsWith(".gitconfig", merge.Destination);
            Assert.Equal("migration:git.config:install", result.ActionEntryIds[cmd.Id]);
            Assert.Equal("git.config#0", result.ActionEntryIds[merge.Id]);
            Assert.Single(result.InstallActionEntries);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    /// <summary>F2 — target ordering follows the existing three RestorePhase values after install planning.</summary>
    [Fact]
    public void Restore_targets_are_ordered_by_phase_stably()
    {
        var (pkg, _, runner, _, _) = Setup("phase");
        string parent = Directory.GetParent(pkg)!.FullName;
        try
        {
            File.Copy(Path.Combine(pkg, "migration", "x", "settings.json"), Path.Combine(pkg, "migration", "x", "first.json"));
            File.Copy(Path.Combine(pkg, "migration", "x", "settings.json"), Path.Combine(pkg, "migration", "x", "install.json"));
            var manifest = new MigrationRestoreManifest(1, new[]
            {
                Target("phase.config", PortabilityClass.ProfileRelative, "config.cfg", source: "migration/x/settings.json", phase: RestorePhase.ConfigWrite),
                Target("phase.first", PortabilityClass.ProfileRelative, "first.cfg", source: "migration/x/first.json", phase: RestorePhase.FirstRunSeed),
                Target("phase.install", PortabilityClass.ProfileRelative, "install.cfg", source: "migration/x/install.json", phase: RestorePhase.Install),
            });

            MigrationRestorePlanResult result = runner.BuildPlan(manifest, pkg, RestoreState.Empty, T0);

            Assert.Equal(new[] { "install.cfg", "first.cfg", "config.cfg" },
                result.Plan.Actions.Cast<RestoreMergeAction>().Select(a => Path.GetFileName(a.Destination)).ToArray());
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    /// <summary>F4 — the report separates manual todo/machine-locked rows from restored rows.</summary>
    [Fact]
    public void RestoreReport_manual_bucket_includes_machine_locked_and_recipe_manual_todo()
    {
        var (pkg, _, runner, _, _) = Setup("report");
        string parent = Directory.GetParent(pkg)!.FullName;
        try
        {
            var meta = new MigrationRecipeMeta(
                UiWarning: new LocalizedText("Token store is not portable.", "Token deposu tasinabilir degil."),
                ManualSteps: Array.Empty<string>(),
                ManualTodo: new[] { "EN: Sign in again. TR: Tekrar giris yapin." },
                InstallerSource: InstallerSource.Winget,
                LicenseSource: LicenseSource.AccountLogin,
                RequiresRelogin: true,
                BackedUpButNotRestored: true,
                SurvivesOnOtherDrive: false);
            var manifest = new MigrationRestoreManifest(1,
                new[] { Target("secret.app", PortabilityClass.MachineLocked, "secret.cfg", tier: RestoreTier.InventoryOnly, meta: meta) });

            RestoreReport report = RestoreReport.FromPlan(runner.BuildPlan(manifest, pkg, RestoreState.Empty, T0));

            Assert.Empty(report.Restored);
            Assert.Empty(report.ReinstallEnqueued);
            Assert.Contains(report.Manual, e => e.Reason == RestoreSkipReason.MachineLocked.ToString());
            Assert.Contains(report.Manual, e => e.Reason == "recipe-manual-todo");
            Assert.Contains(report.Manual, e => e.Reason == "relogin-required");
            Assert.Contains(report.Manual, e => e.Note.Contains("TR:", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }
}

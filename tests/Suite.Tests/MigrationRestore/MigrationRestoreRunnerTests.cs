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
        RestoreStrategy strategy = RestoreStrategy.ConfigWrite, string source = "migration/x/settings.json")
        => new(recipeId, recipeId + "#0", KnownFolder.UserProfile, relativePath, source,
               strategy, RestorePhase.ConfigWrite, Array.Empty<string>(), cls, "sha");

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

    /// <summary>F2 — a profile-relative recipe NOT on the Slice 2 allow-list is skipped (inner-path rebind is Slice 3).</summary>
    [Fact]
    public void Non_allowlisted_recipe_is_skipped_even_when_profile_relative()
    {
        var (pkg, _, runner, _, _) = Setup("allow");
        string parent = Directory.GetParent(pkg)!.FullName;
        try
        {
            var manifest = new MigrationRestoreManifest(1,
                new[] { Target("some.other.app", PortabilityClass.ProfileRelative, ".gitconfig") });
            MigrationRestorePlanResult result = runner.BuildPlan(manifest, pkg, RestoreState.Empty, T0);

            Assert.Empty(result.Plan.Actions);
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
}

using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Tests.MigrationRestore;
using WindowsCareKit.Tests.TestInfra;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests.Step4;

/// <summary>
/// Slice 2 Tier B (sandbox-only, decision §G) — a GENUINE profile-write restore through the PRODUCTION gate
/// (<see cref="ProtectedResources.ForCurrentSystem"/>) + the real executor + the atomic CopyAdapter.Merge,
/// writing into a disposable directory UNDER THE CURRENT USER PROFILE (so the production write-target gate
/// allows it). This must NOT run on a normal host: it is <see cref="DisposableFactAttribute"/> (statically
/// SKIPPED off a disposable machine) and its first statement is the fail-closed guard. On this host it reports
/// SKIPPED, which is correct.
/// </summary>
public class MigrationRestoreDisposableTests
{
    private static readonly DateTime T0 = new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

    [DisposableFact]
    [Trait("Category", TestCategories.Destructive)]
    public void Real_profile_write_restore_places_the_config_and_keeps_a_bak()
    {
        DisposableMachineGuard.RequireDisposableOrSkip();

        // Restore TARGET lives under the real current-user profile (a disposable sub-dir) → the production gate
        // permits the write while keeping the blast radius to a throwaway folder.
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string sandboxRoot = Path.Combine(userProfile, "WindowsCareKit.Tests", "restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandboxRoot);
        try
        {
            // Package with one profile-relative config.
            string pkg = Path.Combine(sandboxRoot, "pkg");
            Directory.CreateDirectory(Path.Combine(pkg, "migration", "git.config"));
            File.WriteAllText(Path.Combine(pkg, "migration", "git.config", ".gitconfig"), "[user]\n name = restored");

            // Fabricated target profile root inside the sandbox; gate uses the PRODUCTION ForCurrentSystem policy.
            string targetProfile = Path.Combine(sandboxRoot, "profile");
            Directory.CreateDirectory(targetProfile);
            File.WriteAllText(Path.Combine(targetProfile, ".gitconfig"), "[user]\n name = preexisting");

            var gate = new SafetyGate(ProtectedResources.ForCurrentSystem(), new Win32PathCanonicalizer());
            var executor = new GatedExecutor(gate, ExecLog(),
                new NoopFileDelete(), new NoopRegistry(), new NoopService(), new NoopTask(), new NoopProcess(),
                new WindowsCareKit.Execution.Adapters.CopyAdapter());

            var runner = new MigrationRestoreRunner(
                new RecipePathResolver(new ProfileRoots(targetProfile,
                    Path.Combine(targetProfile, "AppData", "Roaming"), Path.Combine(targetProfile, "AppData", "Local"))),
                gate);

            var manifest = new MigrationRestoreManifest(1, new[]
            {
                new MigrationRestoreTarget("git.config", "git.config#0", KnownFolder.UserProfile, ".gitconfig",
                    "migration/git.config/.gitconfig", RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite,
                    Array.Empty<string>(), PortabilityClass.ProfileRelative, "sha"),
            });

            MigrationRestorePlanResult result = runner.BuildPlan(manifest, pkg, RestoreState.Empty, T0);
            Assert.Single(result.Plan.Actions);

            ExecutionReport report = executor.ExecuteWithReport(result.Plan, result.Plan.ComputeHash());
            Assert.True(report.Authorized, string.Join(",", report.Results.Select(r => r.Detail)));
            Assert.True(report.Results.All(r => r.Status == ActionStatus.Done));

            string restored = Path.Combine(targetProfile, ".gitconfig");
            Assert.Equal("[user]\n name = restored", File.ReadAllText(restored));
            Assert.Single(Directory.GetFiles(targetProfile, ".gitconfig.bak.*")); // .bak of the preexisting file
        }
        finally { Directory.Delete(sandboxRoot, recursive: true); }
    }

    private static WindowsCareKit.Core.Logging.ExecutionLog ExecLog()
        => new(Path.Combine(Path.GetTempPath(), $"wck-restore-tierb-{Guid.NewGuid():N}.jsonl"),
               new WindowsCareKit.Core.Logging.LogRedactor(null, null));

    private sealed class NoopFileDelete : WindowsCareKit.Execution.Adapters.IFileDeleteAdapter
    { public void Delete(FileDeleteAction a) => throw new InvalidOperationException("not expected"); }
    private sealed class NoopRegistry : WindowsCareKit.Execution.Adapters.IRegistryAdapter
    { public void Delete(RegistryDeleteAction a) => throw new InvalidOperationException("not expected"); }
    private sealed class NoopService : WindowsCareKit.Execution.Adapters.IServiceAdapter
    { public void Apply(ServiceDeleteAction a) => throw new InvalidOperationException("not expected"); }
    private sealed class NoopTask : WindowsCareKit.Execution.Adapters.ITaskAdapter
    { public void Apply(TaskDeleteAction a) => throw new InvalidOperationException("not expected"); }
    private sealed class NoopProcess : WindowsCareKit.Execution.Adapters.IProcessAdapter
    { public void Run(CommandAction a) => throw new InvalidOperationException("not expected"); }
}

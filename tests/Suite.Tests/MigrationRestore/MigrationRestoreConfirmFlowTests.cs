using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests.MigrationRestore;

/// <summary>
/// ConfirmGate + dry-run: building a restore plan is READ-ONLY (no silent overwrite — nothing is written until
/// the plan is approved and executed), and the staged restore plan picks the Medium confirm tier (partial-undo
/// .bak-backed config writes), so the user always sees + confirms exactly what will be overwritten.
/// </summary>
public class MigrationRestoreConfirmFlowTests
{
    private static readonly DateTime T0 = new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Building_the_plan_does_not_write_anything_until_executed()
    {
        string root = MigrationRestoreTestData.TempDir("dryrun");
        try
        {
            string pkg = Path.Combine(root, "pkg");
            Directory.CreateDirectory(Path.Combine(pkg, "migration", "git.config"));
            File.WriteAllText(Path.Combine(pkg, "migration", "git.config", ".gitconfig"), "[user]\n name = a");

            string profile = Path.Combine(root, "Users", "bob");
            Directory.CreateDirectory(profile);

            SafetyGate gate = MigrationRestoreTestData.GateForProfile(profile, Path.Combine(root, "Users"));
            var runner = new MigrationRestoreRunner(
                new RecipePathResolver(new ProfileRoots(profile, Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "Local"))),
                gate);

            var manifest = new MigrationRestoreManifest(1, new[]
            {
                new MigrationRestoreTarget("git.config", "git.config#0", KnownFolder.UserProfile, ".gitconfig",
                    "migration/git.config/.gitconfig", RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite,
                    Array.Empty<string>(), PortabilityClass.ProfileRelative, "sha"),
            });

            MigrationRestorePlanResult result = runner.BuildPlan(manifest, pkg, RestoreState.Empty, T0);

            // The plan was built (dry-run preview), but BUILDING it wrote nothing into the profile.
            Assert.Single(result.Plan.Actions);
            Assert.False(File.Exists(Path.Combine(profile, ".gitconfig")), "no silent overwrite before approval/execution");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_restore_plan_picks_the_Medium_confirm_tier()
    {
        var action = new RestoreMergeAction
        {
            Source = @"C:\pkg\f.cfg", Destination = @"C:\Users\bob\.gitconfig",
            CreateBak = true, Description = "restore", Reason = "t",
            Risk = RiskLevel.Medium, Undo = UndoCapability.Partial,
        };
        var plan = new OperationPlan("Restore migrated settings", "migration-restore", new[] { action }, T0);

        // Partial undo (.bak) + Medium risk → Medium tier: a summary + confirm card, not a silent apply.
        Assert.Equal(ConfirmTier.Medium, ConfirmGateViewModel.TierFor(plan));
    }
}

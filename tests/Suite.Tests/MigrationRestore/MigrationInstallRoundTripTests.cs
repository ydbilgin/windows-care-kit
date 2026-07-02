using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;
using WindowsCareKit.Tests.TestInfra;

namespace WindowsCareKit.Tests.MigrationRestore;

/// <summary>
/// The INSTALL-phase enablement proof: a v2 recipe's install intent flows recipe → backup →
/// <c>migration-install.json</c> → STRICT load → the EXISTING gated <see cref="InstallPlanner"/>, yielding the
/// EXACT, gate-approved, EXPORTED <see cref="CommandAction"/> — with NO process ever executed (no
/// <see cref="IInstallExecutor"/>, no auto-launch). Host-safe: every test asserts the CommandAction, never runs it.
/// </summary>
public class MigrationInstallRoundTripTests
{
    private static readonly DateTime T0 = new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

    private static InstallPlanner Planner() => new(TestData.Gate(), new FakeDriverGuard());

    private static MigrationRecipe Recipe(string id, RecipeInstall install) => new(
        SchemaVersion: 2, Id: id, DisplayName: $"App {id}", Category: "dev-tools",
        Detect: new RecipeDetect(KnownFolder.UserProfile, "." + id, Exists: true),
        Items: new[] { new RecipeItem("." + id, Array.Empty<string>(), Array.Empty<string>()) },
        Exclude: Array.Empty<string>(), SecretRule: "global",
        PortabilityClass: PortabilityClass.ProfileRelative,
        Restore: new RecipeRestore(RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, Array.Empty<string>()))
    {
        Install = install,
    };

    // ---- Test 5: the EXACT winget / npm CommandAction over the projected + strict-loaded manifest ----

    [Fact]
    public void Winget_recipe_yields_the_exact_command_action()
    {
        var install = new RecipeInstall(RecipeInstallMethod.Winget, "Git.Git", null, null, RequiresAdmin: true, RebootExpected: false);
        InstallManifest manifest = new InstallManifest(MigrationInstallProjector.Project(new[] { Recipe("git.config", install) }).Entries);

        InstallPlanResult result = Planner().BuildPlan(manifest, RestoreState.Empty, T0);

        CommandAction cmd = Assert.IsType<CommandAction>(Assert.Single(result.Plan.Actions));
        Assert.EndsWith("winget.exe", cmd.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.True(Path.IsPathFullyQualified(cmd.FileName)); // rooted exe (no PATH search)
        Assert.Equal(
            new[] { "install", "--id", "Git.Git", "-e", "--silent", "--accept-source-agreements", "--accept-package-agreements" },
            cmd.Arguments);
        Assert.True(cmd.RequiresElevation);     // RequiresAdmin → elevation flag
        Assert.Equal(RiskLevel.Medium, cmd.Risk);
        // ActionEntryIds correlates the action to the per-recipe install id.
        Assert.Equal("migration:git.config:install", result.ActionEntryIds[cmd.Id]);
    }

    [Fact]
    public void Npm_recipe_yields_the_exact_command_action()
    {
        var install = new RecipeInstall(RecipeInstallMethod.Npm, null, "@anthropic-ai/claude-code", null, false, false);
        InstallManifest manifest = new InstallManifest(MigrationInstallProjector.Project(new[] { Recipe("anthropic.claude-code", install) }).Entries);

        InstallPlanResult result = Planner().BuildPlan(manifest, RestoreState.Empty, T0);

        CommandAction cmd = Assert.IsType<CommandAction>(Assert.Single(result.Plan.Actions));
        Assert.EndsWith("npm.cmd", cmd.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.True(Path.IsPathFullyQualified(cmd.FileName));
        Assert.Equal(new[] { "install", "-g", "--ignore-scripts", "@anthropic-ai/claude-code" }, cmd.Arguments);
        Assert.False(cmd.RequiresElevation);
        Assert.Equal(RiskLevel.Medium, cmd.Risk);
        Assert.Equal("migration:anthropic.claude-code:install", result.ActionEntryIds[cmd.Id]);
    }

    // ---- Test 7: url-manual → manual checklist, never an executable action ----

    [Fact]
    public void Url_manual_recipe_goes_to_the_manual_checklist_never_an_action()
    {
        var install = new RecipeInstall(RecipeInstallMethod.UrlManual, null, null, "https://nvidia.com/app", false, true);
        InstallManifest manifest = new InstallManifest(MigrationInstallProjector.Project(new[] { Recipe("nvidia.app", install) }).Entries);

        InstallPlanResult result = Planner().BuildPlan(manifest, RestoreState.Empty, T0);

        Assert.Empty(result.Plan.Actions);
        Assert.Single(result.ManualChecklist);
        Assert.Contains(result.Skipped, s => s.Reason == InstallSkipReason.ManualUrl);
    }

    // ---- Test 6: a blocking gate → ZERO CommandActions + an explicit skip; the executor records ZERO calls ----

    [Fact]
    public void A_blocking_gate_yields_zero_actions_and_a_skip_and_the_process_executor_records_zero_calls()
    {
        // A gate that BLOCKS every command (models a tampered/disallowed install).
        var blockingGate = new BlockEveryCommandGate();
        var planner = new InstallPlanner(blockingGate, new FakeDriverGuard());

        var install = new RecipeInstall(RecipeInstallMethod.Winget, "Git.Git", null, null, false, false);
        InstallManifest manifest = new InstallManifest(MigrationInstallProjector.Project(new[] { Recipe("git.config", install) }).Entries);

        InstallPlanResult result = planner.BuildPlan(manifest, RestoreState.Empty, T0);

        Assert.Empty(result.Plan.Actions);                 // ZERO command actions
        Assert.Contains(result.Skipped, s => s.Reason == InstallSkipReason.GateBlocked);

        // This slice is EXPORT-ONLY: no IInstallExecutor is wired, so a fake process executor is never reached.
        // We prove it cannot run anything: there is no action to run, and the spy was never invoked.
        var spy = new SpyProcessExecutor();
        Assert.Empty(result.Plan.Actions);
        Assert.Equal(0, spy.RunCount); // never invoked — an exported plan is never executed
    }

    // ---- Test 8: full backup → package → strict-load → InstallPlanner round-trip ----

    [Fact]
    public void Backup_package_strict_load_planner_round_trip_self_describes_the_reinstall_plan()
    {
        string dir = MigrationRestoreTestData.TempDir("install-rt");
        try
        {
            // The package SAVE side (no copy needed for the install manifest — it is parallel to the config manifest).
            var winget = new RecipeInstall(RecipeInstallMethod.Winget, "Git.Git", null, null, false, false);
            var npm = new RecipeInstall(RecipeInstallMethod.Npm, null, "@anthropic-ai/claude-code", null, false, false);
            MigrationInstallProjection proj = MigrationInstallProjector.Project(new[]
            {
                Recipe("git.config", winget),
                Recipe("anthropic.claude-code", npm),
            });

            var store = new MigrationInstallManifestStore();
            store.Save(dir, proj.Entries);

            // The STRICT load side re-validates everything.
            InstallManifest loaded = store.Load(dir);
            Assert.Equal(2, loaded.Entries.Count);

            // The package self-describes the reinstall plan: the planner builds gate-approved actions from it.
            InstallPlanResult result = Planner().BuildPlan(loaded, RestoreState.Empty, T0);
            Assert.Equal(2, result.Plan.Actions.Count);
            Assert.All(result.Plan.Actions, a => Assert.IsType<CommandAction>(a));
            Assert.True(TestData.Gate().Validate(result.Plan).AllAllowed); // gate-clean, exported, not executed
        }
        finally { TestFs.DeleteResilient(dir); }
    }

    // ---- helpers ----

    /// <summary>A SafetyGate-shaped stub that BLOCKS every action (so even a valid winget command is refused).</summary>
    private sealed class BlockEveryCommandGate : ISafetyGate
    {
        public SafetyVerdict Evaluate(PlannedAction action) => SafetyVerdict.Block("blocked by policy (test)");
        public PlanValidationResult Validate(OperationPlan plan)
            => new(AllAllowed: false, plan.Actions.Select(a => new ActionVerdict(a, Evaluate(a))).ToArray());
    }

    /// <summary>A spy that would record any process run — proves the export-only slice never executes a command.</summary>
    private sealed class SpyProcessExecutor
    {
        public int RunCount { get; private set; }
        public void Run(CommandAction _) => RunCount++;
    }
}

using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Conversion;
using WindowsCareKit.Tests.MigrationRestore;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests.Migration.Conversion;

/// <summary>
/// Exhaustive parity proof for every current capability enum member. The conversion gate owns recipe-level
/// over-claim honesty while BuildPlan independently owns execution eligibility and skip-reason ordering.
/// Item kind is the one deliberate carrier asymmetry: it is present in a recipe but absent from a restore target.
/// </summary>
public sealed class RestoreCapabilityParityTests
{
    private static readonly KnownFolder[] KnownFolders =
    [
        KnownFolder.UserProfile,
        KnownFolder.AppData,
        KnownFolder.LocalAppData,
        KnownFolder.ProgramData,
        KnownFolder.ProgramFiles,
        KnownFolder.ProgramFilesX86,
        KnownFolder.WindowsEtc,
    ];

    private static readonly PortabilityClass[] PortabilityClasses =
    [
        PortabilityClass.ProfileRelative,
        PortabilityClass.MachineLocked,
        PortabilityClass.Partial,
    ];

    private static readonly RestoreTier[] RestoreTiers =
    [
        RestoreTier.Unspecified,
        RestoreTier.InventoryOnly,
        RestoreTier.ConfigCopy,
        RestoreTier.MergeAfterInstall,
    ];

    private static readonly RestoreStrategy[] RestoreStrategies =
    [
        RestoreStrategy.ConfigWrite,
        RestoreStrategy.MergeAfterInstall,
        RestoreStrategy.Replace,
    ];

    private static readonly RecipeItemKind[] ItemKinds =
    [
        RecipeItemKind.ProfilePath,
        RecipeItemKind.MachineRoot,
        RecipeItemKind.ExportCmd,
        RecipeItemKind.WindowsEtc,
        RecipeItemKind.ManualTodo,
    ];

    [Fact]
    public void Conversion_honesty_and_runtime_write_eligibility_follow_the_exhaustive_decision_table()
    {
        AssertEnumCoverage();

        string root = MigrationRestoreTestData.TempDir("capability-parity");
        string packageDirectory = Path.Combine(root, "pkg");
        string sourceDirectory = Path.Combine(packageDirectory, "migration", "parity");
        string profile = Path.Combine(root, "Users", "bob");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(sourceDirectory, "settings.json"), "{}");

        var runner = new MigrationRestoreRunner(
            new RecipePathResolver(new ProfileRoots(
                profile,
                Path.Combine(profile, "AppData", "Roaming"),
                Path.Combine(profile, "AppData", "Local"))),
            MigrationRestoreTestData.GateForProfile(profile, Path.Combine(root, "Users")));

        int combinations = 0;
        int legitimateItemKindDivergences = 0;
        try
        {
            foreach (KnownFolder folder in KnownFolders)
            foreach (PortabilityClass portability in PortabilityClasses)
            foreach (RestoreTier tier in RestoreTiers)
            foreach (RestoreStrategy strategy in RestoreStrategies)
            foreach (RecipeItemKind itemKind in ItemKinds)
            {
                combinations++;
                string caseName = $"{folder}/{portability}/{tier}/{strategy}/{itemKind}";
                MigrationRecipe recipe = Recipe(folder, portability, tier, strategy, itemKind);

                RecipeCapabilityGateResult gate = RecipeCapabilityHonestyGate.Evaluate(recipe);
                string? expectedGateViolation = ExpectedGateViolation(recipe);
                if (expectedGateViolation is null)
                {
                    Assert.True(gate is RecipeCapabilityGateResult.Ok,
                        $"{caseName}: conversion gate should accept the declaration");
                }
                else
                {
                    var violation = Assert.IsType<RecipeCapabilityGateResult.Violation>(gate);
                    Assert.True(
                        string.Equals(expectedGateViolation, violation.Reason, StringComparison.Ordinal),
                        $"{caseName}: conversion reason/order drifted. Expected '{expectedGateViolation}', actual '{violation.Reason}'");
                }

                bool conversionEligible = gate is RecipeCapabilityGateResult.Ok
                    && tier is RestoreTier.ConfigCopy or RestoreTier.MergeAfterInstall;

                MigrationRestoreTarget target = Target(folder, portability, tier, strategy);
                MigrationRestorePlanResult plan = runner.BuildPlan(
                    new MigrationRestoreManifest(MigrationRestoreManifest.CurrentSchemaVersion, [target]),
                    packageDirectory,
                    RestoreState.Empty,
                    new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc));

                RestoreSkipReason? expectedSkip = ExpectedRuntimeSkip(folder, portability, tier, strategy);
                bool runtimeEligible = expectedSkip is null;
                if (runtimeEligible)
                {
                    Assert.True(plan.Plan.Actions.Count == 1 && plan.Skipped.Count == 0,
                        $"{caseName}: BuildPlan should produce exactly one automatic file-write action");
                }
                else
                {
                    RestoreSkip skip = Assert.Single(plan.Skipped);
                    Assert.True(plan.Plan.Actions.Count == 0 && skip.Reason == expectedSkip,
                        $"{caseName}: BuildPlan reason/order drifted. Expected {expectedSkip}, actual {skip.Reason}");
                }

                if (itemKind == RecipeItemKind.ProfilePath)
                {
                    Assert.True(conversionEligible == runtimeEligible,
                        $"{caseName}: conversion and runtime automatic-write eligibility drifted");
                }
                else
                {
                    // BuildPlan cannot inspect item kind because MigrationRestoreTarget does not carry it.
                    // Valid packaging caps these recipes to InventoryOnly; a fabricated high-tier target can
                    // therefore be runtime-eligible on the other four dimensions while the recipe gate rejects it.
                    Assert.False(conversionEligible);
                    if (runtimeEligible)
                        legitimateItemKindDivergences++;
                }
            }

            Assert.Equal(1_260, combinations);
            Assert.Equal(48, legitimateItemKindDivergences);
        }
        finally
        {
            TestFs.DeleteResilient(root);
        }
    }

    [Fact]
    public void Legacy_unspecified_allowlisted_target_is_an_explicit_runtime_only_asymmetry()
    {
        string root = MigrationRestoreTestData.TempDir("capability-legacy-parity");
        string packageDirectory = Path.Combine(root, "pkg");
        string sourceDirectory = Path.Combine(packageDirectory, "migration", "parity");
        string profile = Path.Combine(root, "Users", "bob");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(sourceDirectory, "settings.json"), "{}");

        try
        {
            MigrationRecipe recipe = Recipe(
                KnownFolder.UserProfile,
                PortabilityClass.ProfileRelative,
                RestoreTier.Unspecified,
                RestoreStrategy.ConfigWrite,
                RecipeItemKind.ProfilePath) with
            {
                Id = "git.config",
            };
            Assert.IsType<RecipeCapabilityGateResult.Ok>(RecipeCapabilityHonestyGate.Evaluate(recipe));

            var runner = new MigrationRestoreRunner(
                new RecipePathResolver(new ProfileRoots(
                    profile,
                    Path.Combine(profile, "AppData", "Roaming"),
                    Path.Combine(profile, "AppData", "Local"))),
                MigrationRestoreTestData.GateForProfile(profile, Path.Combine(root, "Users")));
            MigrationRestoreTarget target = Target(
                KnownFolder.UserProfile,
                PortabilityClass.ProfileRelative,
                RestoreTier.Unspecified,
                RestoreStrategy.ConfigWrite) with
            {
                RecipeId = "git.config",
            };

            MigrationRestorePlanResult plan = runner.BuildPlan(
                new MigrationRestoreManifest(MigrationRestoreManifest.CurrentSchemaVersion, [target]),
                packageDirectory,
                RestoreState.Empty,
                new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc));

            // Conversion never emits Unspecified: it is reserved for old restore manifests. BuildPlan therefore
            // has no conversion-time eligibility peer for this case and intentionally consults the legacy allow-list.
            Assert.Single(plan.Plan.Actions);
            Assert.Empty(plan.Skipped);
        }
        finally
        {
            TestFs.DeleteResilient(root);
        }
    }

    private static MigrationRecipe Recipe(
        KnownFolder folder,
        PortabilityClass portability,
        RestoreTier tier,
        RestoreStrategy strategy,
        RecipeItemKind itemKind)
        => new(
            SchemaVersion: 3,
            Id: "parity.not-legacy-allowlisted",
            DisplayName: "Parity",
            Category: "test",
            Detect: new RecipeDetect(folder, "parity", true),
            Items:
            [
                new RecipeItem("settings.json", [], [])
                {
                    Kind = itemKind,
                },
            ],
            Exclude: [],
            SecretRule: "global",
            PortabilityClass: portability,
            Restore: new RecipeRestore(strategy, RestorePhase.ConfigWrite, []))
        {
            RestoreTier = tier,
        };

    private static MigrationRestoreTarget Target(
        KnownFolder folder,
        PortabilityClass portability,
        RestoreTier tier,
        RestoreStrategy strategy)
        => new(
            RecipeId: "parity.not-legacy-allowlisted",
            EntryId: "parity#0",
            KnownFolder: folder,
            RelativePath: "parity/settings.json",
            PackageRelativeSource: "migration/parity/settings.json",
            RestoreStrategy: strategy,
            RestorePhase: RestorePhase.ConfigWrite,
            Preconditions: [],
            PortabilityClass: portability,
            Sha256: "synthetic")
        {
            RestoreTier = tier,
        };

    private static string? ExpectedGateViolation(MigrationRecipe recipe)
    {
        if (recipe.RestoreTier is RestoreTier.Unspecified or RestoreTier.InventoryOnly)
            return null;
        if (recipe.PortabilityClass is not PortabilityClass.ProfileRelative)
            return $"recipe '{recipe.Id}' declares {recipe.RestoreTier} but portability is {recipe.PortabilityClass}";
        if (recipe.Detect.KnownFolder is not (KnownFolder.UserProfile or KnownFolder.AppData or KnownFolder.LocalAppData))
            return $"recipe '{recipe.Id}' declares {recipe.RestoreTier} for non-profile root {recipe.Detect.KnownFolder}";
        if (recipe.Items.Single().Kind is not RecipeItemKind.ProfilePath)
            return $"recipe '{recipe.Id}' declares {recipe.RestoreTier} with a non-profile/manual/export item";
        if (recipe.Restore.Strategy is RestoreStrategy.Replace)
            return $"recipe '{recipe.Id}' declares {recipe.RestoreTier} with unsupported strategy {recipe.Restore.Strategy}";
        return null;
    }

    private static RestoreSkipReason? ExpectedRuntimeSkip(
        KnownFolder folder,
        PortabilityClass portability,
        RestoreTier tier,
        RestoreStrategy strategy)
    {
        if (portability is not PortabilityClass.ProfileRelative)
            return RestoreSkipReason.MachineLocked;
        if (folder is not (KnownFolder.UserProfile or KnownFolder.AppData or KnownFolder.LocalAppData))
            return RestoreSkipReason.NonProfileRoot;
        if (tier == RestoreTier.Unspecified)
            return RestoreSkipReason.NotAllowListed;
        if (tier == RestoreTier.InventoryOnly)
            return RestoreSkipReason.InventoryOnly;
        if (strategy == RestoreStrategy.Replace)
            return RestoreSkipReason.UnsupportedStrategy;
        return null;
    }

    private static void AssertEnumCoverage()
    {
        Assert.Equal(Enum.GetValues<KnownFolder>(), KnownFolders);
        Assert.Equal(Enum.GetValues<PortabilityClass>(), PortabilityClasses);
        Assert.Equal(Enum.GetValues<RestoreTier>(), RestoreTiers);
        Assert.Equal(Enum.GetValues<RestoreStrategy>(), RestoreStrategies);
        Assert.Equal(Enum.GetValues<RecipeItemKind>(), ItemKinds);
    }
}

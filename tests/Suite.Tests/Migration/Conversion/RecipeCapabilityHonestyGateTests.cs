using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Conversion;
using Xunit;
using Xunit.Abstractions;

namespace WindowsCareKit.Tests.Migration.Conversion;

public class RecipeCapabilityHonestyGateTests
{
    private readonly ITestOutputHelper _output;

    public RecipeCapabilityHonestyGateTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Builtin_catalog_has_no_honesty_gate_violations()
    {
        IReadOnlyList<MigrationRecipe> recipes = BuiltinRecipeSource.LoadAll();

        var violations = recipes
            .Select(r => (Recipe: r, Result: RecipeCapabilityHonestyGate.Evaluate(r)))
            .Where(x => x.Result is RecipeCapabilityGateResult.Violation)
            .ToArray();

        Assert.Equal(40, recipes.Count);
        Assert.Empty(violations);
    }

    [Fact]
    public void Gate_rejects_non_vacuous_overclaiming_fixture()
    {
        var recipe = new MigrationRecipe(
            SchemaVersion: 3,
            Id: "over.claim",
            DisplayName: "Over Claim",
            Category: "test",
            Detect: new RecipeDetect(KnownFolder.ProgramData, "Contoso", true),
            Items: [new RecipeItem("Contoso/settings.json", [], [])],
            Exclude: [],
            SecretRule: "global",
            PortabilityClass: PortabilityClass.ProfileRelative,
            Restore: new RecipeRestore(RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, []))
        {
            RestoreTier = RestoreTier.ConfigCopy,
            CatalogTier = CatalogTier.Community,
        };

        var violation = Assert.IsType<RecipeCapabilityGateResult.Violation>(
            RecipeCapabilityHonestyGate.Evaluate(recipe));
        Assert.Contains("non-profile", violation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Legacy_manifest_entries_convert_or_reject_fail_closed()
    {
        string manifestDirectory = FindRepoPath("src", "Suite.App.Wpf", "manifests");
        string[] files = Directory.GetFiles(manifestDirectory, "*.json");
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        int total = 0;
        int converted = 0;
        int rejected = 0;
        int inventoryOnly = 0;

        foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string json = File.ReadAllText(file);
            LegacyManifestFile manifest = System.Text.Json.JsonSerializer.Deserialize<LegacyManifestFile>(json, options)
                ?? new LegacyManifestFile([]);

            foreach (LegacyManifestEntry entry in manifest.Entries)
            {
                total++;
                RecipeConversionResult result = ManifestToRecipeV3.Convert(entry);
                if (result is RecipeConversionResult.Converted ok)
                {
                    converted++;
                    if (ok.Recipe.RestoreTier == RestoreTier.InventoryOnly)
                        inventoryOnly++;

                    RecipeCapabilityGateResult gate = RecipeCapabilityHonestyGate.Evaluate(ok.Recipe);
                    Assert.True(gate is RecipeCapabilityGateResult.Ok, GateFailure(entry, gate));

                    MigrationRecipe loaded = MigrationRecipeLoader.Load(ManifestToRecipeV3Tests.SerializeForLoader(ok.Recipe));
                    Assert.Equal(ok.Recipe.Id, loaded.Id);
                    Assert.Equal(CatalogTier.Community, loaded.CatalogTier);
                }
                else
                {
                    rejected++;
                    var no = Assert.IsType<RecipeConversionResult.Rejected>(result);
                    Assert.False(string.IsNullOrWhiteSpace(no.Reason));
                }
            }
        }

        _output.WriteLine($"legacy manifest conversion summary: total {total}, converted {converted}, rejected {rejected}, inventory-only {inventoryOnly}");
        Assert.Equal(116, total);
        Assert.True(converted > 0);
        Assert.True(rejected > 0);
    }

    private static string GateFailure(LegacyManifestEntry entry, RecipeCapabilityGateResult gate)
        => gate is RecipeCapabilityGateResult.Violation v
            ? $"{entry.Id}: {v.Reason}"
            : $"{entry.Id}: unexpected gate result {gate.GetType().Name}";

    private static string FindRepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository path: " + Path.Combine(parts));
    }
}

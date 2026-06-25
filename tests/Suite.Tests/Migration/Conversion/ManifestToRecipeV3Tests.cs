using System.Text;
using System.Text.Json;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Conversion;
using Xunit;

namespace WindowsCareKit.Tests.Migration.Conversion;

public class ManifestToRecipeV3Tests
{
    [Fact]
    public void Profile_relative_entry_converts_to_loader_clean_community_recipe_without_overclaim()
    {
        var entry = Entry(secretHandling: "normal");

        var converted = AssertConverted(ManifestToRecipeV3.Convert(entry));

        Assert.Equal(3, converted.SchemaVersion);
        Assert.Equal(KnownFolder.AppData, converted.Detect.KnownFolder);
        Assert.Equal("Contoso/App", converted.Detect.Path);
        Assert.Equal(CatalogTier.Community, converted.CatalogTier);
        Assert.Equal(PortabilityClass.ProfileRelative, converted.PortabilityClass);
        Assert.Equal(RestoreTier.ConfigCopy, converted.RestoreTier);
        Assert.IsType<RecipeCapabilityGateResult.Ok>(RecipeCapabilityHonestyGate.Evaluate(converted));

        MigrationRecipe loaded = MigrationRecipeLoader.Load(SerializeForLoader(converted));
        Assert.Equal(converted.Id, loaded.Id);
        Assert.Equal(CatalogTier.Community, loaded.CatalogTier);
    }

    [Fact]
    public void ProgramData_source_is_capped_to_inventory_only()
    {
        var entry = Entry(source: "%PROGRAMDATA%\\Contoso\\App", secretHandling: "normal");

        var converted = AssertConverted(ManifestToRecipeV3.Convert(entry));

        Assert.Equal(KnownFolder.ProgramData, converted.Detect.KnownFolder);
        Assert.Equal(RestoreTier.InventoryOnly, converted.RestoreTier);
        Assert.IsType<RecipeCapabilityGateResult.Ok>(RecipeCapabilityHonestyGate.Evaluate(converted));
    }

    [Theory]
    [InlineData("C:\\Users\\alice\\App")]
    [InlineData("%APPDATA%\\..\\Secret")]
    [InlineData("%WINDIR%\\System32")]
    public void Unsafe_sources_are_rejected(string source)
    {
        var rejected = Assert.IsType<RecipeConversionResult.Rejected>(
            ManifestToRecipeV3.Convert(Entry(source: source)));

        Assert.False(string.IsNullOrWhiteSpace(rejected.Reason));
    }

    [Fact]
    public void Machine_locked_secret_entry_is_inventory_only_and_cannot_claim_works()
    {
        var entry = Entry(
            source: "%USERPROFILE%\\.codex\\auth.json",
            tier: "T3",
            secretHandling: "never-read");

        var converted = AssertConverted(ManifestToRecipeV3.Convert(entry));

        Assert.Equal(PortabilityClass.MachineLocked, converted.PortabilityClass);
        Assert.Equal(RestoreTier.InventoryOnly, converted.RestoreTier);
        Assert.False(PortabilityBadge.Compute(converted.PortabilityClass, hasPreconditions: false).MayClaimWorks);
    }

    [Fact]
    public void Round_trip_converted_recipe_through_strict_loader()
    {
        var converted = AssertConverted(ManifestToRecipeV3.Convert(Entry(secretHandling: "normal")));

        MigrationRecipe loaded = MigrationRecipeLoader.Load(SerializeForLoader(converted));

        Assert.Equal(converted.Id, loaded.Id);
        Assert.Equal(converted.RestoreTier, loaded.RestoreTier);
        Assert.Equal(converted.CatalogTier, loaded.CatalogTier);
        Assert.Equal(converted.Items.Single().Verify!.MaxSizeMB, loaded.Items.Single().Verify!.MaxSizeMB);
    }

    private static LegacyManifestEntry Entry(
        string source = "%APPDATA%\\Contoso\\App",
        string tier = "T2",
        string restoreMode = "merge-after-install",
        string secretHandling = "metadata-only")
        => new()
        {
            Id = "contoso.app",
            Enabled = true,
            Category = "dev-tools",
            Tier = tier,
            Method = "copy",
            Source = source,
            Target = "dev-tools/contoso",
            Include = ["settings.json"],
            Exclude = ["Cache/**"],
            RequiresClosedProcesses = ["contoso.exe"],
            SecretHandling = secretHandling,
            Verify = new LegacyVerify(["settings.json"], 25),
            Restore = new LegacyRestore(40, restoreMode, "Install Contoso first."),
            Description = "Contoso App",
        };

    internal static MigrationRecipe AssertConverted(RecipeConversionResult result)
        => Assert.IsType<RecipeConversionResult.Converted>(result).Recipe;

    internal static string SerializeForLoader(MigrationRecipe recipe)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", recipe.SchemaVersion);
            writer.WriteString("id", recipe.Id);
            writer.WriteString("displayName", recipe.DisplayName);
            writer.WriteString("category", recipe.Category);
            writer.WritePropertyName("detect");
            writer.WriteStartObject();
            writer.WriteString("knownFolder", recipe.Detect.KnownFolder.ToString());
            writer.WriteString("path", recipe.Detect.Path);
            writer.WriteBoolean("exists", recipe.Detect.Exists);
            writer.WriteEndObject();
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (RecipeItem item in recipe.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("path", item.Path);
                WriteStringArray(writer, "include", item.Include);
                WriteStringArray(writer, "exclude", item.Exclude);
                if (item.Kind != RecipeItemKind.ProfilePath)
                    writer.WriteString("kind", ItemKindWire(item.Kind));
                if (item.LibraryDetector is not null)
                    writer.WriteString("libraryDetector", item.LibraryDetector);
                if (item.LauncherId is not null)
                    writer.WriteString("launcherId", item.LauncherId);
                if (item.ExportKind is not null)
                    writer.WriteString("exportKind", item.ExportKind.Value.ToString());
                if (item.ManualTodo.Count > 0)
                    WriteStringArray(writer, "manualTodo", item.ManualTodo);
                if (item.RequiresClosedProcesses.Count > 0)
                    WriteStringArray(writer, "requiresClosedProcesses", item.RequiresClosedProcesses);
                if (item.Verify is not null)
                {
                    writer.WritePropertyName("verify");
                    writer.WriteStartObject();
                    WriteStringArray(writer, "exists", item.Verify.Exists);
                    if (item.Verify.MaxSizeMB is not null)
                        writer.WriteNumber("maxSizeMB", item.Verify.MaxSizeMB.Value);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            WriteStringArray(writer, "exclude", recipe.Exclude);
            writer.WriteString("secretRule", recipe.SecretRule);
            writer.WriteString("portabilityClass", PortabilityWire(recipe.PortabilityClass));
            writer.WritePropertyName("restore");
            writer.WriteStartObject();
            writer.WriteString("strategy", StrategyWire(recipe.Restore.Strategy));
            writer.WriteString("phase", PhaseWire(recipe.Restore.Phase));
            WriteStringArray(writer, "preconditions", recipe.Restore.Preconditions);
            writer.WriteEndObject();
            writer.WriteString("restoreTier", TierWire(recipe.RestoreTier));
            writer.WriteString("catalogTier", recipe.CatalogTier == CatalogTier.Community ? "community" : "trusted");
            if (recipe.MigrationMeta is not null)
            {
                writer.WritePropertyName("migrationMeta");
                writer.WriteStartObject();
                if (recipe.MigrationMeta.UiWarning is not null)
                {
                    writer.WritePropertyName("uiWarning");
                    writer.WriteStartObject();
                    if (recipe.MigrationMeta.UiWarning.En is not null)
                        writer.WriteString("en", recipe.MigrationMeta.UiWarning.En);
                    if (recipe.MigrationMeta.UiWarning.Tr is not null)
                        writer.WriteString("tr", recipe.MigrationMeta.UiWarning.Tr);
                    writer.WriteEndObject();
                }
                WriteStringArray(writer, "manualSteps", recipe.MigrationMeta.ManualSteps);
                WriteStringArray(writer, "manualTodo", recipe.MigrationMeta.ManualTodo);
                if (recipe.MigrationMeta.InstallerSource is not null)
                    writer.WriteString("installerSource", InstallerWire(recipe.MigrationMeta.InstallerSource.Value));
                if (recipe.MigrationMeta.LicenseSource is not null)
                    writer.WriteString("licenseSource", LicenseWire(recipe.MigrationMeta.LicenseSource.Value));
                writer.WriteBoolean("requiresRelogin", recipe.MigrationMeta.RequiresRelogin);
                writer.WriteBoolean("backedUpButNotRestored", recipe.MigrationMeta.BackedUpButNotRestored);
                writer.WriteBoolean("survivesOnOtherDrive", recipe.MigrationMeta.SurvivesOnOtherDrive);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (string value in values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static string PortabilityWire(PortabilityClass value) => value switch
    {
        PortabilityClass.ProfileRelative => "profile-relative",
        PortabilityClass.MachineLocked => "machine-locked",
        PortabilityClass.Partial => "partial",
        _ => value.ToString(),
    };

    private static string StrategyWire(RestoreStrategy value) => value switch
    {
        RestoreStrategy.ConfigWrite => "config-write",
        RestoreStrategy.MergeAfterInstall => "merge-after-install",
        RestoreStrategy.Replace => "replace",
        _ => value.ToString(),
    };

    private static string PhaseWire(RestorePhase value) => value switch
    {
        RestorePhase.FirstRunSeed => "first-run-seed",
        RestorePhase.ConfigWrite => "configWrite",
        RestorePhase.Install => "install",
        _ => value.ToString(),
    };

    private static string TierWire(RestoreTier value) => value switch
    {
        RestoreTier.InventoryOnly => "inventory-only",
        RestoreTier.ConfigCopy => "config-copy",
        RestoreTier.MergeAfterInstall => "merge-after-install",
        _ => value.ToString(),
    };

    private static string ItemKindWire(RecipeItemKind value) => value switch
    {
        RecipeItemKind.ProfilePath => "profilePath",
        RecipeItemKind.MachineRoot => "machineRoot",
        RecipeItemKind.ExportCmd => "exportCmd",
        RecipeItemKind.WindowsEtc => "windowsEtc",
        RecipeItemKind.ManualTodo => "manualTodo",
        _ => value.ToString(),
    };

    private static string InstallerWire(InstallerSource value) => value switch
    {
        InstallerSource.MicrosoftStore => "microsoft-store",
        InstallerSource.ManualDownload => "manual-download",
        InstallerSource.ExistingInstaller => "existing-installer",
        _ => value.ToString().ToLowerInvariant(),
    };

    private static string LicenseWire(LicenseSource value) => value switch
    {
        LicenseSource.AccountLogin => "account-login",
        LicenseSource.ProductKey => "product-key",
        LicenseSource.LicenseFile => "license-file",
        _ => value.ToString().ToLowerInvariant(),
    };
}

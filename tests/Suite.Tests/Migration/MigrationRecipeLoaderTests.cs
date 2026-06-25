using WindowsCareKit.Core.Modules.Migration;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

/// <summary>Strict, fail-closed recipe loader: unknown fields/enums and bad schema are rejected (decision §strict loader).</summary>
public class MigrationRecipeLoaderTests
{
    private const string Valid = """
    {
      "schemaVersion": 1,
      "id": "anthropic.claude-code",
      "displayName": "Claude Code",
      "category": "dev-tools",
      "detect": { "knownFolder": "UserProfile", "path": ".claude", "exists": true },
      "items": [
        { "path": ".claude/CLAUDE.md" },
        { "path": ".claude/projects", "include": ["**/memory/**"], "exclude": ["**/cache/**"] }
      ],
      "exclude": ["**/node_modules/**"],
      "secretRule": "global",
      "portabilityClass": "profile-relative",
      "restore": { "strategy": "merge-after-install", "phase": "configWrite", "preconditions": ["process-closed"] }
    }
    """;

    private static string V3FromValid(string extraRootField = "")
    {
        string suffix = string.IsNullOrEmpty(extraRootField) ? string.Empty : ", " + extraRootField;
        return Valid
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 3")
            .Replace(
                "\"restore\": { \"strategy\": \"merge-after-install\", \"phase\": \"configWrite\", \"preconditions\": [\"process-closed\"] }",
                "\"restore\": { \"strategy\": \"merge-after-install\", \"phase\": \"configWrite\", \"preconditions\": [\"process-closed\"] }, \"restoreTier\": \"config-copy\"" + suffix);
    }

    [Fact]
    public void Loads_a_valid_recipe()
    {
        MigrationRecipe r = MigrationRecipeLoader.Load(Valid);

        Assert.Equal("anthropic.claude-code", r.Id);
        Assert.Equal(KnownFolder.UserProfile, r.Detect.KnownFolder);
        Assert.True(r.Detect.Exists);
        Assert.Equal(2, r.Items.Count);
        Assert.Equal(new[] { "**/memory/**" }, r.Items[1].Include);
        Assert.Equal(PortabilityClass.ProfileRelative, r.PortabilityClass);
        Assert.Equal(RestoreStrategy.MergeAfterInstall, r.Restore.Strategy);
        Assert.Equal(RestorePhase.ConfigWrite, r.Restore.Phase);
        Assert.Equal(new[] { "process-closed" }, r.Restore.Preconditions);
    }

    [Fact]
    public void Rejects_unknown_top_level_field()
    {
        string json = Valid.Replace("\"category\": \"dev-tools\",", "\"category\": \"dev-tools\", \"command\": \"rm -rf /\",");
        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
        Assert.Contains("command", ex.Message);
    }

    [Fact]
    public void Rejects_unknown_nested_field_in_detect()
    {
        string json = Valid.Replace("\"exists\": true", "\"exists\": true, \"script\": \"x\"");
        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
    }

    [Fact]
    public void Rejects_unknown_known_folder_enum()
    {
        string json = Valid.Replace("\"knownFolder\": \"UserProfile\"", "\"knownFolder\": \"ProgramFiles\"");
        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
        Assert.Contains("knownFolder", ex.Message);
    }

    [Fact]
    public void Rejects_unknown_portability_class()
    {
        string json = Valid.Replace("\"profile-relative\"", "\"totally-portable\"");
        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
    }

    [Fact]
    public void Rejects_unknown_restore_strategy()
    {
        string json = Valid.Replace("\"strategy\": \"merge-after-install\"", "\"strategy\": \"run-installer\"");
        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
    }

    [Fact]
    public void Rejects_unknown_restore_phase()
    {
        string json = Valid.Replace("\"phase\": \"configWrite\"", "\"phase\": \"whenever\"");
        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
    }

    // v2 is supported for install; v3 is supported for detection join keys + restore tier/meta.
    [Fact]
    public void Loads_schema_version_2()
    {
        string json = Valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");
        MigrationRecipe r = MigrationRecipeLoader.Load(json);
        Assert.Equal(2, r.SchemaVersion);
        Assert.Null(r.Install); // v2 without an install block is still valid (install is optional)
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Rejects_unsupported_schema_version(int unsupported)
    {
        string json = Valid.Replace("\"schemaVersion\": 1", $"\"schemaVersion\": {unsupported}");
        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
        Assert.Contains("schemaVersion", ex.Message);
    }

    [Fact]
    public void V2_rejects_v3_root_fields()
    {
        string json = Valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2")
            .Replace("\"restore\": { \"strategy\": \"merge-after-install\", \"phase\": \"configWrite\", \"preconditions\": [\"process-closed\"] }",
                "\"restore\": { \"strategy\": \"merge-after-install\", \"phase\": \"configWrite\", \"preconditions\": [\"process-closed\"] }, \"migrationMeta\": { \"requiresRelogin\": true }");

        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
        Assert.Contains("migrationMeta", ex.Message);
    }

    [Theory]
    [InlineData(1, "\"wingetId\": \"Contoso.App\"")]
    [InlineData(1, "\"restoreTier\": \"config-copy\"")]
    [InlineData(1, "\"catalogTier\": \"trusted\"")]
    [InlineData(2, "\"upstreamDataLicense\": \"mit\"")]
    [InlineData(2, "\"installPathHint\": [\"Contoso\"]")]
    [InlineData(2, "\"packageFamilyName\": [\"Contoso_abc\"]")]
    [InlineData(2, "\"migrationMeta\": { \"requiresRelogin\": true }")]
    public void Older_schema_versions_reject_v3_only_root_fields(int schemaVersion, string field)
    {
        string json = Valid
            .Replace("\"schemaVersion\": 1", $"\"schemaVersion\": {schemaVersion}")
            .Replace(
                "\"category\": \"dev-tools\",",
                "\"category\": \"dev-tools\", " + field + ",");

        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
    }

    [Theory]
    [InlineData(1, "\"kind\": \"profilePath\", ")]
    [InlineData(1, "\"requiresClosedProcesses\": [\"app.exe\"], ")]
    [InlineData(2, "\"verify\": { \"maxSizeMB\": 1 }, ")]
    [InlineData(2, "\"manualTodo\": [\"manual\"], ")]
    public void Older_schema_versions_reject_v3_only_item_fields(int schemaVersion, string field)
    {
        string json = Valid
            .Replace("\"schemaVersion\": 1", $"\"schemaVersion\": {schemaVersion}")
            .Replace(
                "{ \"path\": \".claude/CLAUDE.md\" }",
                "{ " + field + "\"path\": \".claude/CLAUDE.md\" }");

        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
    }

    [Fact]
    public void V3_accepts_join_keys_restore_tier_meta_and_item_fields()
    {
        const string json = """
        {
          "schemaVersion": 3,
          "id": "contoso.app",
          "displayName": "Contoso App",
          "category": "utilities",
          "detect": { "knownFolder": "AppData", "path": "Contoso", "exists": true },
          "items": [
            {
              "path": "Contoso/settings.json",
              "include": ["settings.json"],
              "exclude": [],
              "requiresClosedProcesses": ["contoso.exe"],
              "verify": { "exists": ["settings.json"], "maxSizeMB": 25 }
            }
          ],
          "exclude": [],
          "secretRule": "global",
          "portabilityClass": "profile-relative",
          "restore": { "strategy": "merge-after-install", "phase": "configWrite", "preconditions": [] },
          "install": { "method": "install-winget", "wingetId": "Contoso.App" },
          "wingetId": "Contoso.App",
          "productCode": ["{11111111-2222-3333-4444-555555555555}"],
          "upgradeCode": ["{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}"],
          "packageFamilyName": ["Contoso.App_abc123"],
          "installPathHint": ["Contoso App"],
          "restoreTier": "merge-after-install",
          "catalogTier": "trusted",
          "upstreamDataLicense": "mit",
          "migrationMeta": {
            "uiWarning": { "en": "Backs up settings only.", "tr": "Yalnizca ayarlari yedekler." },
            "manualSteps": ["Sign in again"],
            "manualTodo": ["Export secrets manually"],
            "installerSource": "winget",
            "licenseSource": "account-login",
            "requiresRelogin": true,
            "backedUpButNotRestored": false,
            "survivesOnOtherDrive": false
          }
        }
        """;

        MigrationRecipe recipe = MigrationRecipeLoader.Load(json);

        Assert.Equal(3, recipe.SchemaVersion);
        Assert.Equal(RestoreTier.MergeAfterInstall, recipe.RestoreTier);
        Assert.Equal("Contoso.App", recipe.WingetId);
        Assert.Equal("contoso.app_abc123", recipe.PackageFamilyName.Single().ToLowerInvariant());
        Assert.Equal("Contoso App", recipe.InstallPathHint.Single());
        Assert.Equal(CatalogTier.Trusted, recipe.CatalogTier);
        Assert.Equal(UpstreamDataLicense.Mit, recipe.UpstreamDataLicense);
        Assert.Equal(["process-closed:contoso.exe"], RecipeToBackupEntry.Bridge(new ResolvedRecipe(
            recipe,
            true,
            [new ResolvedRecipeItem(@"C:\Users\a\AppData\Roaming\Contoso\settings.json", "migration/contoso.app/Contoso/settings.json", recipe.Items[0].Include, recipe.Items[0].Exclude, recipe.Items[0].Path, recipe.Items[0].RequiresClosedProcesses)],
            [])).Single().Meta.Preconditions);
        Assert.Equal(25, recipe.Items[0].Verify!.MaxSizeMB);
        Assert.Equal("Backs up settings only.", recipe.MigrationMeta!.UiWarning!.En);
        Assert.True(recipe.MigrationMeta.RequiresRelogin);
    }

    [Fact]
    public void V3_forces_inventory_only_for_machine_locked_recipe()
    {
        string json = Valid
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 3")
            .Replace("\"profile-relative\"", "\"machine-locked\"")
            .Replace("\"restore\": { \"strategy\": \"merge-after-install\", \"phase\": \"configWrite\", \"preconditions\": [\"process-closed\"] }",
                "\"restore\": { \"strategy\": \"merge-after-install\", \"phase\": \"configWrite\", \"preconditions\": [\"process-closed\"] }, \"restoreTier\": \"merge-after-install\"");

        MigrationRecipe recipe = MigrationRecipeLoader.Load(json);
        Assert.Equal(RestoreTier.InventoryOnly, recipe.RestoreTier);
    }

    [Fact]
    public void V3_forces_inventory_only_for_machine_root_item()
    {
        const string json = """
        {
          "schemaVersion": 3,
          "id": "steam",
          "displayName": "Steam",
          "category": "games",
          "detect": { "knownFolder": "AppData", "path": "Steam", "exists": false },
          "items": [
            { "kind": "machineRoot", "path": "steam-library", "libraryDetector": "steam", "launcherId": "Steam" }
          ],
          "exclude": [],
          "secretRule": "global",
          "portabilityClass": "partial",
          "restore": { "strategy": "merge-after-install", "phase": "configWrite", "preconditions": [] },
          "restoreTier": "merge-after-install"
        }
        """;

        MigrationRecipe recipe = MigrationRecipeLoader.Load(json);
        Assert.Equal(RestoreTier.InventoryOnly, recipe.RestoreTier);
        Assert.Equal(RecipeItemKind.MachineRoot, recipe.Items.Single().Kind);
    }

    [Fact]
    public void V3_forces_inventory_only_for_non_profile_known_folder()
    {
        string json = V3FromValid()
            .Replace("\"knownFolder\": \"UserProfile\"", "\"knownFolder\": \"ProgramData\"");

        MigrationRecipe recipe = MigrationRecipeLoader.Load(json);

        Assert.Equal(RestoreTier.InventoryOnly, recipe.RestoreTier);
    }

    [Theory]
    [InlineData("\"restoreTier\": \"teleport\"")]
    [InlineData("\"catalogTier\": \"owner\"")]
    [InlineData("\"upstreamDataLicense\": \"public-domain-ish\"")]
    [InlineData("\"migrationMeta\": { \"installerSource\": \"powershell\" }")]
    public void V3_rejects_unknown_new_enum_values(string replacementField)
    {
        string json = replacementField.StartsWith("\"restoreTier\"", StringComparison.Ordinal)
            ? V3FromValid().Replace("\"restoreTier\": \"config-copy\"", replacementField)
            : replacementField.StartsWith("\"upstreamDataLicense\"", StringComparison.Ordinal)
                ? V3FromValid(replacementField)
            : V3FromValid(replacementField);

        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
    }

    [Theory]
    [InlineData("mit", UpstreamDataLicense.Mit)]
    [InlineData("apache-2", UpstreamDataLicense.Apache2)]
    [InlineData("bsd", UpstreamDataLicense.Bsd)]
    [InlineData("gpl", UpstreamDataLicense.Gpl)]
    [InlineData("cc-by", UpstreamDataLicense.CcBy)]
    [InlineData("cc-by-nc-sa", UpstreamDataLicense.CcByNcSa)]
    [InlineData("proprietary", UpstreamDataLicense.Proprietary)]
    [InlineData("none", UpstreamDataLicense.None)]
    [InlineData("unknown", UpstreamDataLicense.Unknown)]
    public void V3_loads_closed_upstream_data_license_values(string wireValue, UpstreamDataLicense expected)
    {
        MigrationRecipe recipe = MigrationRecipeLoader.Load(V3FromValid($"\"upstreamDataLicense\": \"{wireValue}\""));

        Assert.Equal(expected, recipe.UpstreamDataLicense);
    }

    [Fact]
    public void V3_defaults_absent_upstream_data_license_to_unknown()
    {
        MigrationRecipe recipe = MigrationRecipeLoader.Load(V3FromValid());

        Assert.Equal(UpstreamDataLicense.Unknown, recipe.UpstreamDataLicense);
    }

    [Theory]
    [InlineData("C:\\\\Users\\\\alice\\\\App")]
    [InlineData("\\\\\\\\server\\\\share\\\\App")]
    [InlineData("../App")]
    [InlineData("%LOCALAPPDATA%/App")]
    public void V3_rejects_absolute_or_escaping_install_path_hints(string hint)
    {
        string json = V3FromValid($"\"installPathHint\": [\"{hint}\"]");

        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("C:\\\\Windows\\\\secret")]
    [InlineData("\\\\\\\\server\\\\share\\\\secret")]
    [InlineData("%APPDATA%/secret")]
    public void Loader_rejects_item_paths_that_escape_the_declared_root(string path)
    {
        string json = V3FromValid()
            .Replace("\"path\": \".claude/CLAUDE.md\"", $"\"path\": \"{path}\"");

        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
    }

    [Fact]
    public void Machine_root_item_requires_detector_and_launcher_id()
    {
        string json = V3FromValid()
            .Replace(
                "{ \"path\": \".claude/CLAUDE.md\" }",
                "{ \"kind\": \"machineRoot\", \"path\": \"library\" }");

        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
    }

    [Fact]
    public void V3_export_item_uses_closed_kind_not_command_string()
    {
        const string json = """
        {
          "schemaVersion": 3,
          "id": "windows.path",
          "displayName": "PATH dump",
          "category": "system",
          "detect": { "knownFolder": "UserProfile", "path": ".", "exists": false },
          "items": [
            { "kind": "exportCmd", "path": "path-dump", "exportKind": "PathDump" }
          ],
          "exclude": [],
          "secretRule": "global",
          "portabilityClass": "partial",
          "restore": { "strategy": "config-write", "phase": "configWrite", "preconditions": [] },
          "restoreTier": "inventory-only"
        }
        """;

        MigrationRecipe recipe = MigrationRecipeLoader.Load(json);
        Assert.Equal(ExportKind.PathDump, recipe.Items.Single().ExportKind);

        string bad = json.Replace("\"exportKind\": \"PathDump\"", "\"command\": \"reg export HKCU\\\\Software x.reg\"");
        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(bad));
        Assert.Contains("command", ex.Message);
    }

    [Fact]
    public void Rejects_missing_required_field()
    {
        string json = Valid.Replace("\"id\": \"anthropic.claude-code\",", "");
        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
    }

    // ---- v2 install block (decision §FINAL DESIGN 1-2 + Tests list 1) ----

    // A v2 recipe carrying an install block. {0} is the inner install JSON (so each test supplies its own block).
    private const string V2WithInstall = """
    {
      "schemaVersion": 2,
      "id": "anthropic.claude-code",
      "displayName": "Claude Code",
      "category": "dev-tools",
      "detect": { "knownFolder": "UserProfile", "path": ".claude", "exists": true },
      "items": [ { "path": ".claude/CLAUDE.md" } ],
      "exclude": [],
      "secretRule": "global",
      "portabilityClass": "profile-relative",
      "restore": { "strategy": "config-write", "phase": "configWrite", "preconditions": [] },
      "install": { __INSTALL__ }
    }
    """;

    private static string V2(string install) => V2WithInstall.Replace("__INSTALL__", install);

    [Fact]
    public void V1_rejects_an_install_block_as_an_unknown_field()
    {
        // The Valid template is v1; adding install must be rejected as an unknown root field (built-in seeds are v1).
        string json = Valid.Replace(
            "\"restore\": { \"strategy\": \"merge-after-install\", \"phase\": \"configWrite\", \"preconditions\": [\"process-closed\"] }",
            "\"restore\": { \"strategy\": \"merge-after-install\", \"phase\": \"configWrite\", \"preconditions\": [\"process-closed\"] }, \"install\": { \"method\": \"install-winget\", \"wingetId\": \"Anthropic.Claude\" }");
        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
        Assert.Contains("install", ex.Message);
    }

    [Fact]
    public void V2_accepts_install_winget()
    {
        MigrationRecipe r = MigrationRecipeLoader.Load(V2("""
            "method": "install-winget", "wingetId": "Git.Git", "requiresAdmin": true, "rebootExpected": false
            """));
        Assert.NotNull(r.Install);
        Assert.Equal(RecipeInstallMethod.Winget, r.Install!.Method);
        Assert.Equal("Git.Git", r.Install.WingetId);
        Assert.True(r.Install.RequiresAdmin);
        Assert.Null(r.Install.NpmPackage);
        Assert.Null(r.Install.ManualUrl);
    }

    [Fact]
    public void V2_accepts_install_npm()
    {
        MigrationRecipe r = MigrationRecipeLoader.Load(V2("""
            "method": "install-npm", "npmPackage": "@anthropic-ai/claude-code"
            """));
        Assert.NotNull(r.Install);
        Assert.Equal(RecipeInstallMethod.Npm, r.Install!.Method);
        Assert.Equal("@anthropic-ai/claude-code", r.Install.NpmPackage);
        Assert.Null(r.Install.WingetId);
    }

    [Fact]
    public void V2_accepts_install_url_manual()
    {
        MigrationRecipe r = MigrationRecipeLoader.Load(V2("""
            "method": "install-url-manual", "manualUrl": "https://nvidia.com/app"
            """));
        Assert.NotNull(r.Install);
        Assert.Equal(RecipeInstallMethod.UrlManual, r.Install!.Method);
        Assert.Equal("https://nvidia.com/app", r.Install.ManualUrl);
    }

    [Fact]
    public void V2_rejects_unknown_nested_install_field()
    {
        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(V2("""
            "method": "install-winget", "wingetId": "Git.Git", "authCommand": "claude login"
            """)));
        Assert.Contains("authCommand", ex.Message);
    }

    [Fact]
    public void V2_rejects_unknown_install_method()
    {
        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(V2("""
            "method": "install-msi", "wingetId": "Git.Git"
            """)));
        Assert.Contains("install.method", ex.Message);
    }

    [Fact]
    public void V2_rejects_install_that_is_the_wrong_json_kind()
    {
        // install present but a string, not an object.
        string json = V2WithInstall.Replace("\"install\": { __INSTALL__ }", "\"install\": \"install-winget\"");
        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
    }

    [Fact]
    public void V2_rejects_multiple_locators()
    {
        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(V2("""
            "method": "install-winget", "wingetId": "Git.Git", "npmPackage": "left-pad"
            """)));
        Assert.Contains("EXACTLY ONE locator", ex.Message);
    }

    [Fact]
    public void V2_rejects_missing_locator()
    {
        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(V2("""
            "method": "install-winget", "requiresAdmin": true
            """)));
        Assert.Contains("EXACTLY ONE locator", ex.Message);
    }

    [Theory]
    [InlineData("evil/../../id")]   // path
    [InlineData("-e")]              // leading dash → would smuggle a flag into the --id position
    [InlineData("Bad Id")]          // whitespace
    public void V2_rejects_an_invalid_winget_id(string wingetId)
    {
        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(V2($$"""
            "method": "install-winget", "wingetId": "{{wingetId}}"
            """)));
        Assert.Contains("winget", ex.Message);
    }

    [Theory]
    [InlineData("git+https://example.com/x.git")] // git ref (URL also tripwires, but the value is rejected as a name)
    [InlineData("../local/path")]                 // path
    [InlineData("Has Space")]                      // whitespace / invalid name
    public void V2_rejects_an_invalid_npm_value(string npmPackage)
    {
        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(V2($$"""
            "method": "install-npm", "npmPackage": "{{npmPackage}}"
            """)));
        Assert.Contains("npm", ex.Message);
    }

    [Fact]
    public void Rejects_empty_items()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "id": "x",
          "displayName": "X",
          "detect": { "knownFolder": "UserProfile", "path": ".x" },
          "items": [],
          "portabilityClass": "profile-relative",
          "restore": { "strategy": "config-write", "phase": "configWrite" }
        }
        """;
        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
    }

    [Fact]
    public void Rejects_malformed_json()
    {
        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load("{ not json "));
    }

    // ---- recipe-id grammar (decision §"recipe id is not path-validated → package-escape") ----

    [Theory]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("c:evil")]
    [InlineData(".hidden")]   // must START alphanumeric
    [InlineData("-leading")]  // must START alphanumeric
    [InlineData("has space")]
    public void Rejects_recipe_id_that_is_not_a_single_plain_segment(string badId)
    {
        string json = Valid.Replace("\"id\": \"anthropic.claude-code\"", $"\"id\": \"{badId.Replace("\\", "\\\\")}\"");
        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
    }

    [Theory]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("nul")]
    [InlineData("com1")]
    [InlineData("lpt9")]
    [InlineData("con.json")]   // stem before the first '.' is reserved
    public void Rejects_reserved_windows_device_name_id(string reservedId)
    {
        string json = Valid.Replace("\"id\": \"anthropic.claude-code\"", $"\"id\": \"{reservedId}\"");
        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("git.config")]
    [InlineData("anthropic.claude-code")]
    [InlineData("microsoft.vscode")]
    [InlineData("discord")]
    [InlineData("App_123")]
    public void Accepts_a_valid_single_segment_id(string goodId)
    {
        string json = Valid.Replace("\"id\": \"anthropic.claude-code\"", $"\"id\": \"{goodId}\"");
        MigrationRecipe r = MigrationRecipeLoader.Load(json);
        Assert.Equal(goodId, r.Id);
    }

    [Fact]
    public void Every_builtin_recipe_loads_under_the_new_id_grammar()
    {
        // LoadAll runs the strict loader (including the id grammar) over every embedded seed; a built-in id
        // that the new grammar rejected would throw here. This pins that the guard did not break the ship set.
        IReadOnlyList<MigrationRecipe> recipes = BuiltinRecipeSource.LoadAll();
        Assert.NotEmpty(recipes);
        Assert.All(recipes, r => Assert.NotEmpty(r.Id));

        Assert.All(recipes, r => Assert.Equal(3, r.SchemaVersion));
        Assert.Contains(recipes, r => r.Install is not null);
        Assert.All(recipes, r => Assert.NotNull(r.MigrationMeta?.UiWarning));
    }
}

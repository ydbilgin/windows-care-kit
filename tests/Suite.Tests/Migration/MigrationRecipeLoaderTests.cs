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

    // Critic fix #2: v2 is now a SUPPORTED schema version (it adds the optional install block), so it must LOAD.
    // The unsupported-version assertion moved to a version BELOW the floor (v0) and ABOVE the ceiling (v3).
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
    [InlineData(3)]
    public void Rejects_unsupported_schema_version(int unsupported)
    {
        string json = Valid.Replace("\"schemaVersion\": 1", $"\"schemaVersion\": {unsupported}");
        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
        Assert.Contains("schemaVersion", ex.Message);
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

        // Critic fix #5: extend (do NOT duplicate) this test with the v2/install assertions. The shipped seeds
        // are all v1 and carry NO install block — proving v1 still loads and that v1 never grows an install spec.
        Assert.All(recipes, r => Assert.Equal(1, r.SchemaVersion));
        Assert.All(recipes, r => Assert.Null(r.Install));
    }
}

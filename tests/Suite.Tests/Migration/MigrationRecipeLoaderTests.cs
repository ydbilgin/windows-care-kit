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

    [Fact]
    public void Rejects_unsupported_schema_version()
    {
        string json = Valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");
        var ex = Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
        Assert.Contains("schemaVersion", ex.Message);
    }

    [Fact]
    public void Rejects_missing_required_field()
    {
        string json = Valid.Replace("\"id\": \"anthropic.claude-code\",", "");
        Assert.Throws<RecipeValidationException>(() => MigrationRecipeLoader.Load(json));
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
    }
}

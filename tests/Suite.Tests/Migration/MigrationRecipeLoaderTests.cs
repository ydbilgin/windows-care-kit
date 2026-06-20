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
}

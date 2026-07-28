using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

public class InstallManifestLoaderTests
{
    private static InstallManifestLoader Loader => new();
    private static InstallManifest Parse(string json) => Loader.Parse(json).Manifest;

    // MINOR-04: the test this replaced covered BOTH "" and "   "; keep both inputs rather than ship a
    // strictly weaker test than the one that was removed.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_json_is_reported_as_malformed(string json)
    {
        InstallManifestLoadResult result = Loader.Parse(json);

        Assert.Equal(InstallManifestLoadStatus.Malformed, result.Status);
        Assert.Empty(result.Manifest.Entries);
    }

    [Fact]
    public void Malformed_json_is_reported_as_malformed()
    {
        InstallManifestLoadResult result = Loader.Parse("{ not json");

        Assert.Equal(InstallManifestLoadStatus.Malformed, result.Status);
        Assert.Empty(result.Manifest.Entries);
    }

    [Fact]
    public void Load_absent_manifest_reports_not_installed()
    {
        using var ws = new TempWorkspace("wck-install-manifest-absent-");
        string path = Path.Combine(ws.Root, "missing.json");

        InstallManifestLoadResult result = Loader.Load(path);

        Assert.Equal(InstallManifestLoadStatus.NotInstalled, result.Status);
        Assert.Equal(path, result.ManifestPath);
        Assert.Null(result.FailureCategory);
        Assert.Empty(result.Manifest.Entries);
    }

    [Fact]
    public void Load_valid_empty_manifest_is_loaded_not_failed()
    {
        using var ws = new TempWorkspace("wck-install-manifest-empty-");
        string path = Path.Combine(ws.Root, "90-install.json");
        File.WriteAllText(path, """{ "entries": [] }""");

        InstallManifestLoadResult result = Loader.Load(path);

        Assert.Equal(InstallManifestLoadStatus.Loaded, result.Status);
        Assert.Empty(result.Manifest.Entries);
        Assert.Null(result.FailureCategory);
    }

    [Fact]
    public void Load_malformed_manifest_names_path_and_safe_category()
    {
        using var ws = new TempWorkspace("wck-install-manifest-bad-");
        string path = Path.Combine(ws.Root, "90-install.json");
        File.WriteAllText(path, "{ not json");

        InstallManifestLoadResult result = Loader.Load(path);

        Assert.Equal(InstallManifestLoadStatus.Malformed, result.Status);
        Assert.Equal(path, result.ManifestPath);
        Assert.Equal("JsonException", result.FailureCategory);
        Assert.Empty(result.Manifest.Entries);
    }

    [Fact]
    public void Load_unreadable_manifest_names_path_and_safe_category()
    {
        using var ws = new TempWorkspace("wck-install-manifest-locked-");
        string path = Path.Combine(ws.Root, "90-install.json");
        File.WriteAllText(path, """{ "entries": [] }""");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        InstallManifestLoadResult result = Loader.Load(path);

        Assert.Equal(InstallManifestLoadStatus.Unreadable, result.Status);
        Assert.Equal(path, result.ManifestPath);
        Assert.Equal("IOException", result.FailureCategory);
        Assert.Empty(result.Manifest.Entries);
    }

    [Fact]
    public void Parses_core_fields_and_optional_fields()
    {
        var manifest = Parse("""
            { "schemaVersion": 1, "entries": [
              { "id": "install-codex", "phase": "install", "category": "ai-cli", "method": "install-npm",
                "npmPackage": "@openai/codex", "requiresAdmin": false, "rebootExpected": false,
                "installTier": "auto", "requiresNode": true,
                "authProbe": "%USERPROFILE%\\.codex\\auth.json", "authKey": "codex", "authCommand": "codex login",
                "description": "OpenAI Codex CLI" }
            ] }
            """);

        var e = Assert.Single(manifest.Entries);
        Assert.Equal("install-codex", e.Id);
        Assert.Equal("ai-cli", e.Category);
        Assert.Equal(InstallMethod.Npm, e.Method);
        Assert.Equal("@openai/codex", e.NpmPackage);
        Assert.True(e.RequiresNode);
        Assert.Equal(@"%USERPROFILE%\.codex\auth.json", e.AuthProbe);
        Assert.Equal("codex", e.AuthKey);
        Assert.Equal("codex login", e.AuthCommand);
        Assert.True(e.IsAutomatable);
    }

    [Fact]
    public void Manual_after_tier_is_not_automatable()
    {
        var manifest = Parse("""
            { "entries": [
              { "id": "install-vs", "category": "developer", "method": "install-winget",
                "wingetId": "Microsoft.VisualStudio.2022.Community", "installTier": "manual-after" }
            ] }
            """);

        Assert.False(manifest.Entries.Single().IsAutomatable);
    }

    [Fact]
    public void Url_manual_method_is_not_automatable()
    {
        var manifest = Parse("""
            { "entries": [
              { "id": "install-antigravity", "category": "ai-cli", "method": "install-url-manual",
                "manualUrl": "https://antigravity.google/", "installTier": "manual-after" }
            ] }
            """);

        var e = manifest.Entries.Single();
        Assert.False(e.IsAutomatable);
        Assert.Equal("https://antigravity.google/", e.ManualUrl);
    }

    [Fact]
    public void Restore_order_follows_the_category_sequence()
    {
        // ai-cli before developer in the FILE, but developer must get a lower RestoreOrder band.
        var manifest = Parse("""
            { "entries": [
              { "id": "a-ai", "category": "ai-cli", "method": "install-npm", "npmPackage": "x", "installTier": "auto" },
              { "id": "b-dev", "category": "developer", "method": "install-winget", "wingetId": "y", "installTier": "auto" }
            ] }
            """);

        var dev = manifest.Entries.Single(e => e.Id == "b-dev");
        var ai = manifest.Entries.Single(e => e.Id == "a-ai");
        Assert.True(dev.RestoreOrder < ai.RestoreOrder);
    }

    [Fact]
    public void Entries_missing_id_or_method_are_dropped()
    {
        var manifest = Parse("""
            { "entries": [
              { "category": "arac", "method": "install-winget", "wingetId": "x", "installTier": "auto" },
              { "id": "ok", "category": "arac", "method": "install-winget", "wingetId": "y", "installTier": "auto" }
            ] }
            """);

        Assert.Equal("ok", manifest.Entries.Single().Id);
    }

    [Fact]
    public void The_bundled_install_manifest_parses_into_entries()
    {
        // Mirrors the real 90-install.json shape; ensures the schema the planner relies on is honored.
        var manifest = Parse("""
            { "schemaVersion": 1, "entries": [
              { "id": "install-google-chrome", "phase": "install", "category": "browser",
                "method": "install-winget", "wingetId": "Google.Chrome", "installTier": "auto",
                "requiresAdmin": false, "rebootExpected": false, "description": "Chrome" },
              { "id": "install-everything", "phase": "install", "category": "arac",
                "method": "install-winget", "wingetId": "voidtools.Everything", "installTier": "manual-after",
                "requiresAdmin": true, "rebootExpected": false, "description": "Everything" }
            ] }
            """);

        Assert.Equal(2, manifest.Entries.Count);
        Assert.True(manifest.Entries.Single(e => e.Id == "install-google-chrome").IsAutomatable);
        Assert.False(manifest.Entries.Single(e => e.Id == "install-everything").IsAutomatable);
    }

    [Fact]
    public void Load_reads_the_renamed_bundled_install_manifest()
    {
        string path = Path.Combine(FindRepositoryRoot(), "src", "Suite.Module.Install", "manifests", "90-install.json");

        InstallManifestLoadResult result = Loader.Load(path);
        InstallManifest manifest = result.Manifest;

        Assert.Equal(InstallManifestLoadStatus.Loaded, result.Status);
        Assert.NotEmpty(manifest.Entries);
        Assert.Contains(manifest.Entries, e => e.Id == "install-google-chrome" && e.Category == "browser");
        Assert.DoesNotContain(manifest.Entries, e => e.Category.Contains("tarayici", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_finds_the_bundled_install_manifest_at_the_runtime_output_path()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "manifests", "90-install.json");

        Assert.True(File.Exists(path), path);
        InstallManifestLoadResult result = Loader.Load(path);
        InstallManifest manifest = result.Manifest;

        Assert.Equal(InstallManifestLoadStatus.Loaded, result.Status);
        Assert.NotEmpty(manifest.Entries);
        Assert.Contains(manifest.Entries, e => e.Id == "install-google-chrome" && e.Category == "browser");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WindowsCareKit.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("repository root not found");
    }
}

using WindowsCareKit.Core.Modules.Backup;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>A deterministic <see cref="IEnvironmentExpander"/> for backup tests — no real environment touched.</summary>
internal sealed class FakeEnvironmentExpander : IEnvironmentExpander
{
    private readonly Dictionary<string, string> _vars;

    public FakeEnvironmentExpander(Dictionary<string, string>? vars = null)
        => _vars = vars ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["%USERPROFILE%"] = @"C:\Users\alice",
            ["%APPDATA%"] = @"C:\Users\alice\AppData\Roaming",
            ["%LOCALAPPDATA%"] = @"C:\Users\alice\AppData\Local",
            ["%WINDIR%"] = @"C:\Windows",
        };

    public string Expand(string path)
    {
        string result = path;
        foreach (var (token, value) in _vars)
            result = result.Replace(token, value, StringComparison.OrdinalIgnoreCase);
        return result;
    }
}

public class ManifestLoaderTests
{
    private static ManifestLoader Loader() => new(new FakeEnvironmentExpander());

    [Fact]
    public void Loads_entries_and_expands_env_in_source()
    {
        const string json = """
        {
          "entries": [
            {
              "id": "chrome-profile",
              "enabled": true,
              "method": "copy",
              "category": "browser",
              "source": "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\Default",
              "target": "browser/Chrome/Default",
              "exclude": ["Cache/**"],
              "secretHandling": "metadata-only",
              "restore": { "order": 52, "mode": "merge-after-install" },
              "description": "Chrome profile"
            }
          ]
        }
        """;

        BackupManifest manifest = Loader().LoadFromJson(new[] { json });

        BackupEntry e = Assert.Single(manifest.Entries);
        Assert.Equal("chrome-profile", e.Id);
        Assert.True(e.Enabled);
        Assert.Equal(@"C:\Users\alice\AppData\Local\Google\Chrome\User Data\Default", e.Source);
        Assert.Equal("browser/Chrome/Default", e.Target);
        Assert.Equal(52, e.RestoreOrder);
        Assert.Equal("merge-after-install", e.RestoreMode);
        Assert.Contains("Cache/**", e.Exclude);
        Assert.True(e.IsCopyable);
    }

    [Fact]
    public void Never_read_and_disabled_are_not_copyable()
    {
        const string json = """
        {
          "entries": [
            { "id": "codex-auth", "enabled": false, "method": "copy", "source": "%USERPROFILE%\\.codex\\auth.json",
              "target": "ai/.codex/auth.json", "secretHandling": "never-read", "uiWarning": "TOKEN" },
            { "id": "claude-json", "enabled": true, "method": "copy", "source": "%USERPROFILE%\\.claude.json",
              "target": "ai/.claude.json", "secretHandling": "never-read" }
          ]
        }
        """;

        BackupManifest manifest = Loader().LoadFromJson(new[] { json });

        BackupEntry disabledSecret = manifest.Entries.First(e => e.Id == "codex-auth");
        BackupEntry enabledSecret = manifest.Entries.First(e => e.Id == "claude-json");

        Assert.False(disabledSecret.IsCopyable);
        Assert.False(enabledSecret.IsCopyable);           // never-read forbids copy even when enabled
        Assert.True(enabledSecret.IsManualTodo);           // it becomes a manual to-do
        Assert.Equal("TOKEN", disabledSecret.UiWarning);
    }

    [Fact]
    public void Install_and_export_and_manual_methods_classify_correctly()
    {
        const string json = """
        {
          "entries": [
            { "id": "vscode-install", "enabled": true, "method": "install-winget", "source": "" },
            { "id": "wifi-export", "enabled": true, "method": "export-cmd", "source": "" },
            { "id": "crypto-wallet", "enabled": true, "method": "manual-todo", "secretHandling": "manual-only", "source": "" }
          ]
        }
        """;

        BackupManifest manifest = Loader().LoadFromJson(new[] { json });

        Assert.True(manifest.Entries.First(e => e.Id == "vscode-install").IsInstall);
        Assert.False(manifest.Entries.First(e => e.Id == "wifi-export").IsCopyable);   // export-cmd is listed only
        Assert.True(manifest.Entries.First(e => e.Id == "crypto-wallet").IsManualTodo);
    }

    [Fact]
    public void Malformed_document_is_skipped_not_fatal()
    {
        const string good = """{ "entries": [ { "id": "a", "enabled": true, "method": "copy", "source": "%WINDIR%\\x", "target": "t" } ] }""";
        const string bad = "{ this is not json";

        BackupManifest manifest = Loader().LoadFromJson(new[] { bad, good });

        Assert.Single(manifest.Entries);
        Assert.Equal("a", manifest.Entries[0].Id);
    }

    [Fact]
    public void Multiple_documents_merge_in_order()
    {
        const string a = """{ "entries": [ { "id": "a", "method": "copy", "enabled": true, "source": "%WINDIR%\\a", "target": "a" } ] }""";
        const string b = """{ "entries": [ { "id": "b", "method": "copy", "enabled": true, "source": "%WINDIR%\\b", "target": "b" } ] }""";

        BackupManifest manifest = Loader().LoadFromJson(new[] { a, b });

        Assert.Equal(2, manifest.Entries.Count);
        Assert.Equal("a", manifest.Entries[0].Id);
        Assert.Equal("b", manifest.Entries[1].Id);
    }

    [Fact]
    public void LoadFromDirectory_discovers_renamed_backup_manifests_by_glob()
    {
        string manifestDirectory = Path.Combine(FindRepositoryRoot(), "src", "Suite.App.Wpf", "manifests");
        string[] expectedFiles =
        [
            "00-ai-tools.json",
            "10-developer.json",
            "20-browser.json",
            "30-games.json",
            "40-system.json",
            "50-notes.json",
            "60-wsl.json",
            "70-general-user.json",
            "80-network-drive.json",
            "90-install.json",
        ];
        foreach (string file in expectedFiles)
            Assert.True(File.Exists(Path.Combine(manifestDirectory, file)), file);

        BackupManifest manifest = Loader().LoadFromDirectory(manifestDirectory);

        Assert.NotEmpty(manifest.Entries);
        Assert.Contains(manifest.Entries, e => e.Id == "vscode-user");
        Assert.Contains(manifest.Entries, e => e.Id == "firefox-profiles");
        Assert.Contains(manifest.Entries, e => e.Id == "network-driver-export");
        Assert.DoesNotContain(manifest.Entries, e => e.Id.StartsWith("install-", StringComparison.Ordinal));
        Assert.DoesNotContain(manifest.Entries, e => e.Category.Contains("tarayici", StringComparison.OrdinalIgnoreCase));
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

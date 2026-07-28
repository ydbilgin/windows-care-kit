using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Tests.TestInfra;
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

        BackupManifest manifest = Loader().LoadFromJson(new[] { json }).Manifest;

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

        BackupManifest manifest = Loader().LoadFromJson(new[] { json }).Manifest;

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

        BackupManifest manifest = Loader().LoadFromJson(new[] { json }).Manifest;

        Assert.True(manifest.Entries.First(e => e.Id == "vscode-install").IsInstall);
        Assert.False(manifest.Entries.First(e => e.Id == "wifi-export").IsCopyable);   // export-cmd is listed only
        Assert.True(manifest.Entries.First(e => e.Id == "crypto-wallet").IsManualTodo);
    }

    [Fact]
    public void Malformed_document_is_skipped_not_fatal()
    {
        const string good = """{ "entries": [ { "id": "a", "enabled": true, "method": "copy", "source": "%WINDIR%\\x", "target": "t" } ] }""";
        const string bad = "{ this is not json";

        BackupManifestLoadResult result = Loader().LoadFromJson(new[] { bad, good });
        BackupManifest manifest = result.Manifest;

        Assert.Equal(BackupManifestLoadStatus.Partial, result.Status);
        Assert.Equal(BackupManifestFileStatus.Malformed, result.Files[0].Status);
        Assert.Equal("JsonException", result.Files[0].FailureCategory);
        Assert.Single(manifest.Entries);
        Assert.Equal("a", manifest.Entries[0].Id);
    }

    [Fact]
    public void Absent_directory_reports_not_installed()
    {
        using var ws = new TempWorkspace("wck-backup-manifest-absent-");
        string path = Path.Combine(ws.Root, "missing");

        BackupManifestLoadResult result = Loader().LoadFromDirectory(path);

        Assert.Equal(BackupManifestLoadStatus.NotInstalled, result.Status);
        Assert.Empty(result.Manifest.Entries);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void Valid_zero_entry_file_is_complete_and_recorded_as_loaded()
    {
        using var ws = new TempWorkspace("wck-backup-manifest-empty-");
        string path = Path.Combine(ws.Root, "00-empty.json");
        File.WriteAllText(path, """{ "entries": [] }""");

        BackupManifestLoadResult result = Loader().LoadFromDirectory(ws.Root);

        Assert.Equal(BackupManifestLoadStatus.Complete, result.Status);
        Assert.Empty(result.Manifest.Entries);
        BackupManifestFileOutcome file = Assert.Single(result.Files);
        Assert.Equal(path, file.Path);
        Assert.Equal(BackupManifestFileStatus.Loaded, file.Status);
        Assert.Null(file.FailureCategory);
    }

    [Fact]
    public void Malformed_file_is_unavailable_and_names_the_file()
    {
        using var ws = new TempWorkspace("wck-backup-manifest-bad-");
        string path = Path.Combine(ws.Root, "00-bad.json");
        File.WriteAllText(path, "{ not json");

        BackupManifestLoadResult result = Loader().LoadFromDirectory(ws.Root);

        Assert.Equal(BackupManifestLoadStatus.Unavailable, result.Status);
        Assert.Empty(result.Manifest.Entries);
        BackupManifestFileOutcome file = Assert.Single(result.Files);
        Assert.Equal(path, file.Path);
        Assert.Equal(BackupManifestFileStatus.Malformed, file.Status);
        Assert.Equal("JsonException", file.FailureCategory);
    }

    [Fact]
    public void Unreadable_file_is_unavailable_and_names_the_file()
    {
        using var ws = new TempWorkspace("wck-backup-manifest-locked-");
        string path = Path.Combine(ws.Root, "00-locked.json");
        File.WriteAllText(path, """{ "entries": [] }""");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        BackupManifestLoadResult result = Loader().LoadFromDirectory(ws.Root);

        Assert.Equal(BackupManifestLoadStatus.Unavailable, result.Status);
        Assert.Empty(result.Manifest.Entries);
        BackupManifestFileOutcome file = Assert.Single(result.Files);
        Assert.Equal(path, file.Path);
        Assert.Equal(BackupManifestFileStatus.Unreadable, file.Status);
        Assert.Equal("IOException", file.FailureCategory);
    }

    [Fact]
    public void One_malformed_and_two_good_files_returns_good_entries_and_records_bad_file()
    {
        using var ws = new TempWorkspace("wck-backup-manifest-partial-");
        string badPath = Path.Combine(ws.Root, "10-bad.json");
        File.WriteAllText(Path.Combine(ws.Root, "00-good.json"),
            """{ "entries": [ { "id": "a", "enabled": true, "method": "copy", "source": "a", "target": "a" } ] }""");
        File.WriteAllText(badPath, "{ not json");
        File.WriteAllText(Path.Combine(ws.Root, "20-good.json"),
            """{ "entries": [ { "id": "b", "enabled": true, "method": "copy", "source": "b", "target": "b" } ] }""");

        BackupManifestLoadResult result = Loader().LoadFromDirectory(ws.Root);

        Assert.Equal(BackupManifestLoadStatus.Partial, result.Status);
        Assert.Equal(["a", "b"], result.Manifest.Entries.Select(e => e.Id));
        BackupManifestFileOutcome failure = Assert.Single(result.Files,
            f => f.Status == BackupManifestFileStatus.Malformed);
        Assert.Equal(badPath, failure.Path);
        Assert.Equal("JsonException", failure.FailureCategory);
    }

    /// <summary>MAJOR-04: the malformed branch's continuation is proven above; the UNREADABLE branch is a
    /// separate try/catch/continue and was only ever exercised with a single file, so nothing stopped its
    /// <c>continue</c> becoming a <c>return</c>. Under that regression one locked manifest truncates the whole
    /// plan — every later good entry silently vanishes — while the aggregate still reports the softer
    /// <c>Partial</c>, "may be incomplete". For a recovery tool that is worse than the defect this change closed,
    /// so the sweep must be shown to survive an unreadable file with good manifests on BOTH sides of it.</summary>
    [Fact]
    public void One_unreadable_and_two_good_files_returns_good_entries_and_records_locked_file()
    {
        using var ws = new TempWorkspace("wck-backup-manifest-locked-partial-");
        string lockedPath = Path.Combine(ws.Root, "10-locked.json");
        File.WriteAllText(Path.Combine(ws.Root, "00-good.json"),
            """{ "entries": [ { "id": "a", "enabled": true, "method": "copy", "source": "a", "target": "a" } ] }""");
        File.WriteAllText(lockedPath, """{ "entries": [] }""");
        File.WriteAllText(Path.Combine(ws.Root, "20-good.json"),
            """{ "entries": [ { "id": "b", "enabled": true, "method": "copy", "source": "b", "target": "b" } ] }""");
        using var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        BackupManifestLoadResult result = Loader().LoadFromDirectory(ws.Root);

        Assert.Equal(BackupManifestLoadStatus.Partial, result.Status);
        Assert.Equal(["a", "b"], result.Manifest.Entries.Select(e => e.Id));
        BackupManifestFileOutcome failure = Assert.Single(result.Files,
            f => f.Status == BackupManifestFileStatus.Unreadable);
        Assert.Equal(lockedPath, failure.Path);
        Assert.Equal("IOException", failure.FailureCategory);
    }

    [Fact]
    public void Multiple_documents_merge_in_order()
    {
        const string a = """{ "entries": [ { "id": "a", "method": "copy", "enabled": true, "source": "%WINDIR%\\a", "target": "a" } ] }""";
        const string b = """{ "entries": [ { "id": "b", "method": "copy", "enabled": true, "source": "%WINDIR%\\b", "target": "b" } ] }""";

        BackupManifest manifest = Loader().LoadFromJson(new[] { a, b }).Manifest;

        Assert.Equal(2, manifest.Entries.Count);
        Assert.Equal("a", manifest.Entries[0].Id);
        Assert.Equal("b", manifest.Entries[1].Id);
    }

    [Fact]
    public void LoadFromDirectory_discovers_renamed_backup_manifests_by_glob()
    {
        string repositoryRoot = FindRepositoryRoot();
        string backupManifestDirectory = Path.Combine(repositoryRoot, "src", "Suite.Module.Backup", "manifests");
        string installManifestPath = Path.Combine(repositoryRoot, "src", "Suite.Module.Install", "manifests", "90-install.json");
        foreach (string file in BackupManifestFiles)
            Assert.True(File.Exists(Path.Combine(backupManifestDirectory, file)), file);
        Assert.True(File.Exists(installManifestPath), installManifestPath);

        using var ws = new TempWorkspace("wck-backup-manifests-");
        foreach (string file in BackupManifestFiles)
            File.Copy(Path.Combine(backupManifestDirectory, file), Path.Combine(ws.Root, file));
        File.Copy(installManifestPath, Path.Combine(ws.Root, "90-install.json"));

        BackupManifest manifest = Loader().LoadFromDirectory(ws.Root).Manifest;

        Assert.NotEmpty(manifest.Entries);
        Assert.Contains(manifest.Entries, e => e.Id == "vscode-user");
        Assert.Contains(manifest.Entries, e => e.Id == "firefox-profiles");
        Assert.Contains(manifest.Entries, e => e.Id == "network-driver-export");
        Assert.DoesNotContain(manifest.Entries, e => e.Id.StartsWith("install-", StringComparison.Ordinal));
        Assert.DoesNotContain(manifest.Entries, e => e.Category.Contains("tarayici", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Module_manifest_content_lands_in_runtime_manifests_directory_and_skips_install_manifest()
    {
        string manifestsDirectory = Path.Combine(AppContext.BaseDirectory, "manifests");
        Assert.True(Directory.Exists(manifestsDirectory), manifestsDirectory);
        foreach (string file in BackupManifestFiles)
            Assert.True(File.Exists(Path.Combine(manifestsDirectory, file)), file);
        Assert.True(File.Exists(Path.Combine(manifestsDirectory, "90-install.json")), "90-install.json");

        BackupManifest manifest = Loader().LoadFromDirectory(manifestsDirectory).Manifest;

        Assert.NotEmpty(manifest.Entries);
        Assert.Contains(manifest.Entries, e => e.Id == "vscode-user");
        Assert.Contains(manifest.Entries, e => e.Id == "firefox-profiles");
        Assert.Contains(manifest.Entries, e => e.Id == "network-driver-export");
        Assert.DoesNotContain(manifest.Entries, e => e.Id.StartsWith("install-", StringComparison.Ordinal));
    }

    private static readonly string[] BackupManifestFiles =
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
    ];

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

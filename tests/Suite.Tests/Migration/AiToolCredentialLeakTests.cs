using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution.Adapters;
using Xunit;
using WindowsCareKit.Tests.TestInfra;

namespace WindowsCareKit.Tests.Migration;

/// <summary>
/// END-TO-END proof that AI-CLI credential/token files are excluded AT COPY TIME from the broad
/// <c>.claude</c>/<c>.codex</c>/<c>.gemini</c> backup recipes — closing the gap a council audit found
/// (2026-07-01): the manifest recipes broad-copy <c>%USERPROFILE%\.codex</c> and <c>%USERPROFILE%\.gemini</c>
/// excluding only log/cache dirs, and the name-based <see cref="SecretGlobOverlay"/> did NOT match
/// <c>auth.json</c> (Codex OAuth token) or <c>oauth_creds.json</c> (Gemini OAuth token) — so those token files
/// rode into the recovery package. A backup tool whose headline is "always excludes secrets" cannot ship that.
///
/// The engine-level E2E test seeds the real credential leaf names inside an otherwise-included tree and runs
/// the real <see cref="CopyAdapter"/> supplying ONLY cache-style excludes (NO secret overlay) — proving the
/// copy ENGINE prunes them built-in, so the legacy Backup path that never plumbs the overlay cannot leak. A
/// second test drives the real <see cref="BackupPlanner"/> → <see cref="CopyAdapter"/> shipped path end-to-end.
/// </summary>
public class AiToolCredentialLeakTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "wck-aicred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Codex_and_Gemini_oauth_token_files_do_not_leak_into_the_package()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);

            // Benign config that MUST survive (the reason the folder is backed up at all).
            File.WriteAllText(Path.Combine(src, "config.toml"), "model = \"x\"");
            File.WriteAllText(Path.Combine(src, "settings.json"), "{}");

            // Credential/token leaf NAMES the broad copy would otherwise grab (none matched the pre-fix globs).
            // Contents are inert decoys — the test asserts on the file NAME being pruned, never on real tokens.
            File.WriteAllText(Path.Combine(src, "auth.json"), "{\"note\":\"decoy\"}");           // Codex token file
            File.WriteAllText(Path.Combine(src, "oauth_creds.json"), "{\"note\":\"decoy\"}");    // Gemini token file
            File.WriteAllText(Path.Combine(src, ".npmrc"), "registry=https://example.test");     // npm auth file
            File.WriteAllText(Path.Combine(src, "id_ed25519"), "decoy");                          // non-RSA SSH key
            File.WriteAllText(Path.Combine(src, ".env"), "note=decoy");                           // env secrets file
            File.WriteAllText(Path.Combine(src, "cred_blob_a.bin"), "decoy");                     // documented leak pattern

            // Regression: names already covered must stay excluded.
            File.WriteAllText(Path.Combine(src, ".credentials.json"), "PRIVATE");                            // *credential*
            File.WriteAllText(Path.Combine(src, "id_rsa"), "PRIVATE");                                        // id_*

            string dst = Path.Combine(root, "dst");

            // Simulate the LEGACY backup path (cx cross-family review finding): the manifest recipe supplies
            // ONLY its own cache excludes and does NOT plumb the secret overlay. The engine must add the
            // overlay BUILT-IN, so the token files are still pruned even though the caller never listed them.
            new CopyAdapter().Copy(new CopyAction
            {
                Source = src,
                Destination = dst,
                ExcludeLeaves = new List<string> { "log/**", "cache/**" }, // real .codex/.gemini recipe excludes — NO overlay
                Description = "legacy manifest backup",
                Reason = "ai-tool credential leak test (engine-built-in overlay)",
            });

            // Benign config survives.
            Assert.True(File.Exists(Path.Combine(dst, "config.toml")), "benign config.toml should be copied");
            Assert.True(File.Exists(Path.Combine(dst, "settings.json")), "benign settings.json should be copied");

            // No credential/token file leaked.
            Assert.False(File.Exists(Path.Combine(dst, "auth.json")), "Codex auth.json (OAuth token) leaked into the package");
            Assert.False(File.Exists(Path.Combine(dst, "oauth_creds.json")), "Gemini oauth_creds.json (OAuth token) leaked into the package");
            Assert.False(File.Exists(Path.Combine(dst, ".npmrc")), ".npmrc (_authToken) leaked into the package");
            Assert.False(File.Exists(Path.Combine(dst, "id_ed25519")), "id_ed25519 private key leaked into the package");
            Assert.False(File.Exists(Path.Combine(dst, ".env")), ".env secrets leaked into the package");
            Assert.False(File.Exists(Path.Combine(dst, "cred_blob_a.bin")), "cred_blob_*.bin leaked into the package");
            Assert.False(File.Exists(Path.Combine(dst, ".credentials.json")), ".credentials.json leaked (regression)");
            Assert.False(File.Exists(Path.Combine(dst, "id_rsa")), "id_rsa leaked (regression)");
        }
        finally { TestFs.DeleteResilient(root); }
    }

    [Theory]
    [InlineData("auth.json")]
    [InlineData("oauth_creds.json")]
    [InlineData(".npmrc")]
    [InlineData("id_ed25519")]
    [InlineData("id_ecdsa")]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("cred_blob_a.bin")]
    public void New_ai_tool_credential_leaves_are_classified_secret(string leaf)
        => Assert.True(MigrationSecretFilter.IsSecretLeafName(leaf), $"{leaf} must be classified as a secret leaf");

    [Theory]
    [InlineData("config.toml")]
    [InlineData("settings.json")]
    [InlineData("CLAUDE.md")]
    [InlineData("keybindings.json")]
    public void Benign_config_leaves_are_not_classified_secret(string leaf)
        => Assert.False(MigrationSecretFilter.IsSecretLeafName(leaf), $"{leaf} must NOT be classified as a secret leaf");

    /// <summary>
    /// SHIPPED-PATH proof (cx cross-family review finding): the real legacy Backup flow
    /// (<see cref="BackupPlanner"/> → <see cref="CopyAdapter"/>) — the path that loads
    /// <c>00-ai-araclari.json</c> — must prune the AI-CLI token even though the manifest recipe supplies ONLY
    /// its own cache excludes and never plumbs the secret overlay. This exercises the planner→executor wiring,
    /// not just <c>CopyAdapter</c> in isolation.
    /// </summary>
    [Fact]
    public void Shipped_backup_planner_path_prunes_codex_auth_token_from_a_broad_copy_recipe()
    {
        string root = TempDir();
        try
        {
            // A source tree mimicking %USERPROFILE%\.codex — benign config + a real OAuth token.
            string src = Path.Combine(root, "dotcodex");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "config.toml"), "model = \"x\"");
            File.WriteAllText(Path.Combine(src, "auth.json"), "{\"note\":\"decoy\"}"); // inert decoy — asserted by name, not content

            // A codex-home-style entry: broad copy of the whole tree, supplying NO secret overlay — exactly the
            // shipped 00-ai-araclari.json recipe (the legacy Backup path). SecretHandling.Normal => it IS copied.
            var entry = new BackupEntry(
                Id: "codex-home", Enabled: true, Method: BackupMethod.Copy, Category: "ai-araclari",
                Source: src, Target: "ai-araclari/.codex",
                Exclude: Array.Empty<string>(),
                SecretHandling: SecretHandling.Normal, RestoreOrder: 44, RestoreMode: "merge-after-install",
                Description: "Codex CLI settings", UiWarning: null);

            string payload = Path.Combine(root, "payload");
            var planner = new BackupPlanner(RealGate(), new FakeEnvironmentExpander());
            BackupPlanResult result = planner.BuildPlan(
                new BackupManifest(new[] { entry }), payload, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var copy = Assert.IsType<CopyAction>(Assert.Single(result.Plan.Actions));
            new CopyAdapter().Copy(copy);

            string dstDir = Path.Combine(payload, "ai-araclari", ".codex");
            Assert.True(File.Exists(Path.Combine(dstDir, "config.toml")), "benign config.toml should be backed up");
            Assert.False(File.Exists(Path.Combine(dstDir, "auth.json")), "Codex auth.json leaked through the SHIPPED BackupPlanner path");
        }
        finally { TestFs.DeleteResilient(root); }
    }

    private static ISafetyGate RealGate()
        => new SafetyGate(ProtectedResources.ForCurrentSystem(), new FakeCanonicalizer());
}

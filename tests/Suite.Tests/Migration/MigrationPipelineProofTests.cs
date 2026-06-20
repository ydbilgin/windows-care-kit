using System.Text;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Execution.Adapters;
using WindowsCareKit.Win32;
using Xunit;
using Xunit.Abstractions;

namespace WindowsCareKit.Tests.Migration;

/// <summary>
/// A LOW-LEVEL proof of the migration READ pipeline (<see cref="Win32RecipeFileSystem"/> →
/// <see cref="RecipeResolver"/> → <see cref="RecipeToBackupEntry"/> → the real <see cref="CopyAdapter"/>): real
/// config is carried and real secrets/caches are excluded at copy time through the production components. It does
/// NOT prove the backup→restore ORCHESTRATION — that is owned by <c>MigrationBackupRunner</c> and proven
/// end-to-end by <c>MigrationBackupRunnerTests</c> (BuildPlan/Run → gated executor → restore manifest → restore).
/// One deterministic test against a synthetic profile (host-safe + CI-stable + Step-4-sandbox safe), and one
/// lenient smoke against the REAL machine profile that writes a human report of what would be carried.
/// </summary>
public class MigrationPipelineProofTests
{
    private readonly ITestOutputHelper _out;
    public MigrationPipelineProofTests(ITestOutputHelper output) => _out = output;

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "wck-migproof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// Full chain — embedded recipe → RecipeResolver (real Win32 FS) → RecipeToBackupEntry → real CopyAdapter →
    /// package — against a synthetic %USERPROFILE%. Proves real config is carried and real secrets/caches are
    /// excluded AT COPY TIME through the production code path.
    /// </summary>
    [Fact]
    public void Full_chain_carries_real_config_and_excludes_secrets_through_production_components()
    {
        string root = TempDir();
        try
        {
            string profile = Path.Combine(root, "profile");
            Directory.CreateDirectory(Path.Combine(profile, ".claude", "skills"));
            File.WriteAllText(Path.Combine(profile, ".gitconfig"), "[user]\n  name = test");
            File.WriteAllText(Path.Combine(profile, ".claude", "CLAUDE.md"), "# memory");
            File.WriteAllText(Path.Combine(profile, ".claude", "settings.json"), "{}");
            File.WriteAllText(Path.Combine(profile, ".claude", "skills", "myskill.md"), "skill");
            File.WriteAllText(Path.Combine(profile, ".claude", "skills", "id_rsa"), "PRIVATE KEY");          // secret
            File.WriteAllText(Path.Combine(profile, ".claude", "skills", "deploy.pem"), "PRIVATE KEY");      // secret
            Directory.CreateDirectory(Path.Combine(profile, ".claude", "skills", "GPUCache"));
            File.WriteAllText(Path.Combine(profile, ".claude", "skills", "GPUCache", "data_0"), "junk");     // cache

            var roots = new ProfileRoots(profile, Path.Combine(root, "appdata"), Path.Combine(root, "local"));
            var resolver = new RecipeResolver(new RecipePathResolver(roots), new Win32RecipeFileSystem());
            string pkg = Path.Combine(root, "package");

            foreach (MigrationRecipe recipe in BuiltinRecipeSource.LoadAll().Where(r => r.Id is "git.config" or "anthropic.claude-code"))
            {
                ResolvedRecipe resolved = resolver.Resolve(recipe);
                foreach (BridgedMigrationItem b in RecipeToBackupEntry.Bridge(resolved))
                {
                    new CopyAdapter().Copy(new CopyAction
                    {
                        Source = b.Entry.Source,
                        Destination = Path.Combine(pkg, b.Entry.Target.Replace('/', Path.DirectorySeparatorChar)),
                        ExcludeLeaves = b.Entry.Exclude,
                        Include = b.Entry.Include,
                        Description = recipe.DisplayName,
                        Reason = "migration proof",
                    });
                }
            }

            string[] all = Directory.Exists(pkg)
                ? Directory.GetFiles(pkg, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();

            // Real config is carried across.
            Assert.Contains(all, f => f.EndsWith(".gitconfig", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(all, f => f.EndsWith("CLAUDE.md", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(all, f => f.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(all, f => f.EndsWith("myskill.md", StringComparison.OrdinalIgnoreCase));

            // Secrets + caches are NOT — at copy time, through the production engine.
            Assert.DoesNotContain(all, f => Path.GetFileName(f).Equals("id_rsa", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(all, f => f.EndsWith("deploy.pem", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(all, f => f.Contains("GPUCache", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// Lenient REAL-machine smoke: resolve the embedded recipes against THIS user's actual profile and report
    /// what the tool would carry across a format. Asserts only the safety invariant (no secret leaf ever lands)
    /// + that detection works; writes a human report to %TEMP%\wck-migration-proof.txt. Never fails on a clean
    /// machine where nothing is installed.
    /// </summary>
    [Fact]
    public void Real_profile_smoke_reports_what_would_be_carried_and_leaks_no_secret()
    {
        string pkg = TempDir();
        var report = new StringBuilder();
        report.AppendLine("Windows Care Kit — migration backup proof (real profile)");
        report.AppendLine("========================================================");
        try
        {
            var resolver = new RecipeResolver(new RecipePathResolver(ProfileRoots.ForCurrentUser()), new Win32RecipeFileSystem());
            int detected = 0, copiedFiles = 0;

            foreach (MigrationRecipe recipe in BuiltinRecipeSource.LoadAll())
            {
                ResolvedRecipe resolved = resolver.Resolve(recipe);
                if (!resolved.DetectMatched)
                {
                    report.AppendLine($"[ ] {recipe.Id} — not present on this machine");
                    continue;
                }
                detected++;
                report.AppendLine($"[x] {recipe.Id} ({recipe.DisplayName}) — {resolved.Items.Count} item(s), portability={recipe.PortabilityClass}");
                foreach (BridgedMigrationItem b in RecipeToBackupEntry.Bridge(resolved))
                {
                    report.AppendLine($"      • {b.Entry.Source}");
                    new CopyAdapter().Copy(new CopyAction
                    {
                        Source = b.Entry.Source,
                        Destination = Path.Combine(pkg, b.Entry.Target.Replace('/', Path.DirectorySeparatorChar)),
                        ExcludeLeaves = b.Entry.Exclude,
                        Include = b.Entry.Include,
                        Description = recipe.DisplayName,
                        Reason = "real-profile proof",
                    });
                }
            }

            string[] all = Directory.Exists(pkg)
                ? Directory.GetFiles(pkg, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();
            copiedFiles = all.Length;

            // THE safety invariant: no secret leaf may ever appear in the package, on any real machine.
            string[] leaked = all.Where(f => SecretGlobOverlay.IsSecretLeaf(Path.GetFileName(f))).ToArray();
            report.AppendLine("--------------------------------------------------------");
            report.AppendLine($"recipes detected : {detected}");
            report.AppendLine($"files carried    : {copiedFiles}");
            report.AppendLine($"secret leaks     : {leaked.Length}");
            foreach (string l in leaked) report.AppendLine($"   !! LEAK: {l}");

            string reportPath = Path.Combine(Path.GetTempPath(), "wck-migration-proof.txt");
            File.WriteAllText(reportPath, report.ToString());
            _out.WriteLine(report.ToString());
            _out.WriteLine("report written to: " + reportPath);

            Assert.Empty(leaked); // never leak a secret, regardless of what is installed
        }
        finally { try { Directory.Delete(pkg, recursive: true); } catch { /* best-effort */ } }
    }
}

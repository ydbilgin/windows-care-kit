using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Execution.Adapters;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

/// <summary>
/// END-TO-END proof that the migration secret-glob overlay (security review F3) is enforced AT COPY TIME —
/// not merely present as strings in <c>BackupEntry.Exclude</c>. The real <see cref="CopyAdapter"/> is run over
/// a tree of decoy secrets + a cache dir; only the benign config file may land in the package.
///
/// This is the test the adversarial audit said was missing: the prior Migration tests only asserted the glob
/// STRINGS appeared in the exclude list, while <c>CopyAdapter</c> matched <c>ExcludeLeaves</c> as EXACT leaves
/// — so a real <c>id_rsa</c>/<c>mykey.key</c> would have been copied. This test FAILS on that exact-match code
/// and PASSES once the engine treats '*'-bearing ExcludeLeaves entries as leaf globs.
/// </summary>
public class SecretGlobCopyEnforcementTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "wck-secretglob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Copy_excludes_real_secret_and_cache_leaves_via_the_glob_overlay()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);

            // The ONLY file that should survive into the package.
            File.WriteAllText(Path.Combine(src, "settings.json"), "{}");

            // Real secrets whose LEAF names match the secret globs (none is literally "*.key").
            File.WriteAllText(Path.Combine(src, "id_rsa"), "PRIVATE");                 // id_rsa*
            File.WriteAllText(Path.Combine(src, "mykey.key"), "PRIVATE");              // *.key
            File.WriteAllText(Path.Combine(src, "server.pem"), "PRIVATE");            // *.pem
            File.WriteAllText(Path.Combine(src, "apptoken.json"), "PRIVATE");         // *token*
            File.WriteAllText(Path.Combine(src, "my.credentials.txt"), "PRIVATE");    // *credential*
            File.WriteAllText(Path.Combine(src, "session.ppk"), "PRIVATE");           // *.ppk

            // A cache dir (recipe-declared exclude as a leaf glob) with content.
            Directory.CreateDirectory(Path.Combine(src, "GPUCache"));
            File.WriteAllText(Path.Combine(src, "GPUCache", "data_0"), "junk");

            string dst = Path.Combine(root, "dst");

            // Exactly what the bridge produces: the global secret-glob overlay + a recipe cache exclude.
            var excludeLeaves = new List<string>(SecretGlobOverlay.Globs) { "*Cache*" };

            new CopyAdapter().Copy(new CopyAction
            {
                Source = src,
                Destination = dst,
                ExcludeLeaves = excludeLeaves,
                Description = "migration backup",
                Reason = "secret-glob enforcement test",
            });

            Assert.True(File.Exists(Path.Combine(dst, "settings.json")), "benign config should be copied");
            Assert.False(File.Exists(Path.Combine(dst, "id_rsa")), "id_rsa private key leaked into the package");
            Assert.False(File.Exists(Path.Combine(dst, "mykey.key")), "*.key leaked into the package");
            Assert.False(File.Exists(Path.Combine(dst, "server.pem")), "*.pem leaked into the package");
            Assert.False(File.Exists(Path.Combine(dst, "apptoken.json")), "*token* leaked into the package");
            Assert.False(File.Exists(Path.Combine(dst, "my.credentials.txt")), "*credential* leaked into the package");
            Assert.False(File.Exists(Path.Combine(dst, "session.ppk")), "*.ppk leaked into the package");
            Assert.False(Directory.Exists(Path.Combine(dst, "GPUCache")), "cache dir leaked into the package");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Include_allowlist_can_NOT_pull_back_a_secret_leaf()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "id_rsa"), "PRIVATE");
            File.WriteAllText(Path.Combine(src, "ok.json"), "{}");

            string dst = Path.Combine(root, "dst");

            // A hostile/careless recipe explicitly includes everything — exclusion must still win (forbidden-first).
            new CopyAdapter().Copy(new CopyAction
            {
                Source = src,
                Destination = dst,
                Include = new[] { "**" },
                ExcludeLeaves = new List<string>(SecretGlobOverlay.Globs),
                Description = "migration backup",
                Reason = "forbidden-first test",
            });

            Assert.True(File.Exists(Path.Combine(dst, "ok.json")));
            Assert.False(File.Exists(Path.Combine(dst, "id_rsa")), "include allow-list must NOT override the secret-glob exclusion");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

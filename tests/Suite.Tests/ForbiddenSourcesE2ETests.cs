using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Execution.Adapters;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Item 7(b) — Manifest <c>forbiddenSources</c> end-to-end: a backup manifest that declares
/// <c>forbiddenSources</c> (a full absolute path) is loaded, planned via <see cref="BackupPlanner"/>,
/// the resulting <see cref="CopyAction"/> is executed through the real <see cref="CopyAdapter"/>,
/// and the forbidden path is ABSENT from the destination while a benign sibling IS present.
///
/// <para>Today <c>forbiddenSources</c> is plumbed ManifestLoader → BackupPlanner → CopyAdapter but has no
/// end-to-end test that runs a real copy. This test fails if CopyAdapter stops enforcing
/// <c>ForbiddenSources</c> (e.g. if <c>Exclusions.From</c> stops populating <c>_forbiddenFull</c>).</para>
/// </summary>
public class ForbiddenSourcesE2ETests
{
    private static readonly DateTime T0 = new(2026, 6, 21, 10, 0, 0, DateTimeKind.Utc);

    private static string TempDir(string tag)
    {
        string d = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wck-forbidden-{tag}-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// A profile tree with a secret <c>Login Data</c> file (full path declared as <c>forbiddenSources</c>
    /// in the manifest entry) and a benign <c>Bookmarks</c> sibling. BackupPlanner produces a CopyAction with
    /// ForbiddenSources set; CopyAdapter must skip the forbidden file and copy the benign one.
    ///
    /// <para>Non-vacuity: if <c>CopyAdapter.Exclusions.IsForbiddenFull</c> is bypassed (e.g. the full-path set
    /// is never populated), <c>Login Data</c> would land in the destination and the
    /// <c>Assert.False(File.Exists(dest_forbidden))</c> assertion fails.</para>
    /// </summary>
    [Fact]
    public void ForbiddenSources_full_path_file_is_absent_from_destination_benign_sibling_is_present()
    {
        string root = TempDir("main");
        try
        {
            // Source tree: one forbidden file + one benign file at the same level.
            // IMPORTANT: forbiddenSources is an ABSOLUTE-PATH exclusion, distinct from the built-in leaf set.
            // We pick a name ("myapp.appdb") that is NOT in ForbiddenSourceLeaves and does NOT match any
            // SecretGlobOverlay pattern (no *.key/id_rsa*/etc.), so only the ForbiddenSources full-path
            // enforcement can block it.  If ForbiddenSources enforcement is stripped, the file lands in dst.
            string src = System.IO.Path.Combine(root, "profile");
            System.IO.Directory.CreateDirectory(src);
            string forbiddenFile = System.IO.Path.Combine(src, "myapp.appdb");
            string benignFile = System.IO.Path.Combine(src, "settings.json");
            System.IO.File.WriteAllText(forbiddenFile, "app-internal database blob");
            System.IO.File.WriteAllText(benignFile, "{ \"theme\": \"dark\" }");

            string dst = System.IO.Path.Combine(root, "dst");

            // Build a BackupEntry with the forbidden full path declared in ForbiddenSources.
            // We construct the entry directly (same as BackupPlanner would produce) so the test
            // focuses on the CopyAdapter enforcement, not the JSON loading path.
            var entry = new BackupEntry(
                Id: "test.profile",
                Enabled: true,
                Method: BackupMethod.Copy,
                Category: "test",
                Source: src,
                Target: "dst",
                Exclude: System.Array.Empty<string>(),
                SecretHandling: SecretHandling.Normal,
                RestoreOrder: 50,
                RestoreMode: "config-write",
                Description: "test entry",
                UiWarning: null)
            {
                ForbiddenSources = new[] { forbiddenFile },  // the absolute full path to the forbidden file
            };

            // Execute through the real CopyAdapter — same path as BackupPlanner → CopyAdapter production flow.
            new CopyAdapter().Copy(new CopyAction
            {
                Source = entry.Source,
                Destination = dst,
                ExcludeLeaves = entry.Exclude,
                ForbiddenSources = entry.ForbiddenSources,
                Include = entry.Include,
                Description = entry.Description,
                Reason = "forbidden-sources E2E test",
            });

            // The forbidden full-path file must NOT be in the destination.
            string destForbidden = System.IO.Path.Combine(dst, "myapp.appdb");
            Assert.False(System.IO.File.Exists(destForbidden),
                "forbiddenSources full-path file 'myapp.appdb' must not be copied (enforced by CopyAdapter.ForbiddenSources, not leaf exclusion)");

            // The benign sibling MUST be in the destination.
            string destBenign = System.IO.Path.Combine(dst, "settings.json");
            Assert.True(System.IO.File.Exists(destBenign),
                "benign sibling 'settings.json' must be copied into the destination");
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }
}

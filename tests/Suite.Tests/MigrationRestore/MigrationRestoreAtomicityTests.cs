using WindowsCareKit.Core.Planning;
using WindowsCareKit.Execution.Adapters;
using Xunit;
using WindowsCareKit.Tests.TestInfra;

namespace WindowsCareKit.Tests.MigrationRestore;

/// <summary>
/// F3 — crash-atomic restore write. The CopyAdapter.Merge now stages to a sibling temp + atomic File.Replace,
/// so an interrupted restore leaves EITHER the old file or the complete new one, never a torn file; the .bak is
/// still written. These prove .bak is taken on overwrite, the swap is atomic (no leftover staging file on the
/// happy path), and a re-run after a simulated crash converges to a consistent state (resume).
/// </summary>
public class MigrationRestoreAtomicityTests
{
    private static string TempDir() => MigrationRestoreTestData.TempDir("atomic");

    [Fact]
    public void Overwrite_writes_a_bak_and_swaps_in_the_new_content_with_no_leftover_temp()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "new.cfg");
            string dst = Path.Combine(root, "live.cfg");
            File.WriteAllText(src, "NEW");
            File.WriteAllText(dst, "OLD");

            new CopyAdapter().Merge(new RestoreMergeAction
            {
                Source = src, Destination = dst, CreateBak = true, Description = "m", Reason = "t",
            });

            Assert.Equal("NEW", File.ReadAllText(dst));                 // atomically swapped
            string bak = Assert.Single(Directory.GetFiles(root, "live.cfg.bak.*"));
            Assert.Equal("OLD", File.ReadAllText(bak));                 // old content preserved
            Assert.Empty(Directory.GetFiles(root, "*.wcktmp"));        // no torn/leftover staging file
        }
        finally { TestFs.DeleteResilient(root); }
    }

    [Fact]
    public void First_write_with_no_existing_destination_is_atomic_and_leaves_no_temp()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "new.cfg");
            string dst = Path.Combine(root, "sub", "live.cfg");
            File.WriteAllText(src, "FRESH");

            new CopyAdapter().Merge(new RestoreMergeAction { Source = src, Destination = dst, Description = "m", Reason = "t" });

            Assert.Equal("FRESH", File.ReadAllText(dst));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(dst)!, "*.wcktmp"));
        }
        finally { TestFs.DeleteResilient(root); }
    }

    /// <summary>
    /// Resume after a simulated crash: a leftover <c>.wcktmp</c> staging file from a previous interrupted run
    /// must NOT corrupt the destination — re-running the merge converges to the complete new content and clears
    /// the staging file. The destination, if it existed, was never left torn.
    /// </summary>
    [Fact]
    public void Re_running_after_a_leftover_staging_file_converges_to_consistent_state()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "new.cfg");
            string dst = Path.Combine(root, "live.cfg");
            File.WriteAllText(src, "NEW");
            File.WriteAllText(dst, "OLD");                              // intact old config (crash before swap)
            File.WriteAllText(dst + ".wcktmp", "PARTIAL-GARBAGE");      // leftover staging from the crashed run

            // The destination still holds the consistent OLD content (atomicity: never torn mid-write).
            Assert.Equal("OLD", File.ReadAllText(dst));

            // Re-run: overwrites the stale staging file, swaps atomically, clears the temp.
            new CopyAdapter().Merge(new RestoreMergeAction
            {
                Source = src, Destination = dst, CreateBak = true, Description = "m", Reason = "t",
            });

            Assert.Equal("NEW", File.ReadAllText(dst));
            Assert.Empty(Directory.GetFiles(root, "*.wcktmp"));
        }
        finally { TestFs.DeleteResilient(root); }
    }
}

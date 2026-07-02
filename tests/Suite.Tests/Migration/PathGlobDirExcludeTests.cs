using WindowsCareKit.Core.Planning;
using WindowsCareKit.Execution.Adapters;
using Xunit;
using WindowsCareKit.Tests.TestInfra;

namespace WindowsCareKit.Tests.Migration;

/// <summary>
/// Proof that DIRECTORY path-glob excludes (<c>sessions/**</c>, <c>log/**</c>, <c>cache/**</c>,
/// <c>antigravity*/**</c>) declared by a recipe actually prune their subtree at copy time (adversarial-review
/// finding, 2026-07-01): these were previously compiled as LEAF globs and were INERT — a bare directory leaf
/// has no <c>/</c>, so <c>sessions/**</c> never matched and the <c>.codex</c>/<c>.gemini</c>/<c>.claude</c>
/// recipes' own log/session/cache excludes were dead, copying those trees wholesale (bulk + privacy).
/// Also proves a single-level <c>/*</c> does NOT over-prune deeper content it does not cover.
/// </summary>
public class PathGlobDirExcludeTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "wck-pathglob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Seed(string dir, string relFile, string content = "x")
    {
        string full = Path.Combine(dir, relFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void Directory_double_star_excludes_prune_the_whole_subtree()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            Seed(src, "config.toml");                 // benign — survives
            Seed(src, "sessions/a.json");             // excluded (sessions/**)
            Seed(src, "sessions/deep/b.json");        // excluded (deep under sessions)
            Seed(src, "log/run.txt");                 // excluded (log/**)
            Seed(src, "cache/blob");                  // excluded (cache/**)
            Seed(src, "antigravity_x/state.bin");     // excluded (antigravity*/**, wildcard segment)

            string dst = Path.Combine(root, "dst");
            new CopyAdapter().Copy(new CopyAction
            {
                Source = src,
                Destination = dst,
                ExcludeLeaves = new List<string> { "sessions/**", "log/**", "cache/**", "antigravity*/**" },
                Description = "recipe with directory excludes",
                Reason = "path-glob dir exclude test",
            });

            Assert.True(File.Exists(Path.Combine(dst, "config.toml")), "benign config should be copied");
            Assert.False(Directory.Exists(Path.Combine(dst, "sessions")), "sessions/** subtree should be pruned (dir + contents)");
            Assert.False(Directory.Exists(Path.Combine(dst, "log")), "log/** subtree should be pruned");
            Assert.False(Directory.Exists(Path.Combine(dst, "cache")), "cache/** subtree should be pruned");
            Assert.False(Directory.Exists(Path.Combine(dst, "antigravity_x")), "antigravity*/** subtree should be pruned");
        }
        finally { TestFs.DeleteResilient(root); }
    }

    [Fact]
    public void Single_level_star_excludes_direct_children_but_not_deeper_content()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            Seed(src, "top/direct.txt");       // excluded by top/* (direct child)
            Seed(src, "top/sub/keep.txt");     // NOT covered by top/* — must survive

            string dst = Path.Combine(root, "dst");
            new CopyAdapter().Copy(new CopyAction
            {
                Source = src,
                Destination = dst,
                ExcludeLeaves = new List<string> { "top/*" },
                Description = "single-level exclude",
                Reason = "no over-prune test",
            });

            Assert.False(File.Exists(Path.Combine(dst, "top", "direct.txt")), "top/* should exclude the direct child");
            Assert.True(File.Exists(Path.Combine(dst, "top", "sub", "keep.txt")), "top/* must NOT over-prune deeper content it does not cover");
        }
        finally { TestFs.DeleteResilient(root); }
    }
}

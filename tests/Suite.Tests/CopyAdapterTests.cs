using WindowsCareKit.Core.Planning;
using WindowsCareKit.Execution.Adapters;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>Copy + merge over real temp dirs: copies a tree, never blind-overwrites (writes a .bak), guards secrets.</summary>
public class CopyAdapterTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "wck-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Copies_a_file()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "a.txt");
            string dst = Path.Combine(root, "out", "a.txt");
            File.WriteAllText(src, "hello");

            new CopyAdapter().Copy(new CopyAction
            {
                Source = src,
                Destination = dst,
                Description = "copy",
                Reason = "test",
            });

            Assert.Equal("hello", File.ReadAllText(dst));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Copies_a_directory_tree()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(Path.Combine(src, "nested"));
            File.WriteAllText(Path.Combine(src, "top.txt"), "1");
            File.WriteAllText(Path.Combine(src, "nested", "deep.txt"), "2");
            string dst = Path.Combine(root, "dst");

            new CopyAdapter().Copy(new CopyAction { Source = src, Destination = dst, Description = "copy", Reason = "t" });

            Assert.Equal("1", File.ReadAllText(Path.Combine(dst, "top.txt")));
            Assert.Equal("2", File.ReadAllText(Path.Combine(dst, "nested", "deep.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Merge_backs_up_an_existing_destination_to_a_bak_before_overwriting()
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
                Source = src,
                Destination = dst,
                CreateBak = true,
                Description = "merge",
                Reason = "t",
            });

            Assert.Equal("NEW", File.ReadAllText(dst)); // destination updated
            string? bak = Directory.GetFiles(root, "live.cfg.bak.*").SingleOrDefault();
            Assert.NotNull(bak);
            Assert.Equal("OLD", File.ReadAllText(bak!)); // old content preserved in the .bak
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Merge_without_an_existing_destination_just_writes_the_source()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "new.cfg");
            string dst = Path.Combine(root, "sub", "live.cfg");
            File.WriteAllText(src, "NEW");

            new CopyAdapter().Merge(new RestoreMergeAction { Source = src, Destination = dst, Description = "m", Reason = "t" });

            Assert.Equal("NEW", File.ReadAllText(dst));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(dst)!, "*.bak.*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Refuses_to_copy_a_known_secret_store()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "Login Data");
            File.WriteAllText(src, "secret");

            // The refusal is a typed ForbiddenSourceException (still an InvalidOperationException) so the
            // Backup report can classify the skip on a stable token, not a fragile message substring (L8).
            var ex = Assert.Throws<ForbiddenSourceException>(() => new CopyAdapter().Copy(new CopyAction
            {
                Source = src,
                Destination = Path.Combine(root, "out"),
                Description = "copy",
                Reason = "t",
            }));
            Assert.IsAssignableFrom<InvalidOperationException>(ex);
            Assert.Equal(nameof(ForbiddenSourceException), ForbiddenSourceException.TypeToken);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Copying_a_browser_profile_tree_leaves_every_secret_store_behind()
    {
        string root = TempDir();
        try
        {
            // A realistic Chromium profile: harmless files + credential/cookie/autofill stores,
            // including the modern nested Default\Network\Cookies location.
            string profile = Path.Combine(root, "Default");
            Directory.CreateDirectory(Path.Combine(profile, "Network"));
            File.WriteAllText(Path.Combine(profile, "Bookmarks"), "{}");
            File.WriteAllText(Path.Combine(profile, "Preferences"), "{}");
            File.WriteAllText(Path.Combine(profile, "Login Data"), "PW");
            File.WriteAllText(Path.Combine(profile, "Web Data"), "CARDS");
            File.WriteAllText(Path.Combine(profile, "Cookies"), "COOKIE");
            File.WriteAllText(Path.Combine(profile, "Network", "Cookies"), "COOKIE2");
            // A token folder excluded via the per-action manifest exclude list.
            Directory.CreateDirectory(Path.Combine(profile, "Sync Data"));
            File.WriteAllText(Path.Combine(profile, "Sync Data", "tok"), "TOKEN");

            string dst = Path.Combine(root, "payload", "Default");

            new CopyAdapter().Copy(new CopyAction
            {
                Source = profile,
                Destination = dst,
                ExcludeLeaves = new[] { "Sync Data" }, // manifest-declared exclude
                Description = "copy",
                Reason = "t",
            });

            // Harmless files copied.
            Assert.True(File.Exists(Path.Combine(dst, "Bookmarks")));
            Assert.True(File.Exists(Path.Combine(dst, "Preferences")));
            // Every secret store left behind — including the nested Network\Cookies.
            Assert.False(File.Exists(Path.Combine(dst, "Login Data")));
            Assert.False(File.Exists(Path.Combine(dst, "Web Data")));
            Assert.False(File.Exists(Path.Combine(dst, "Cookies")));
            Assert.False(File.Exists(Path.Combine(dst, "Network", "Cookies")));
            Assert.False(Directory.Exists(Path.Combine(dst, "Sync Data")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Include_allowlist_copies_only_matching_paths()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "profile");
            Directory.CreateDirectory(Path.Combine(src, "extensions", "ext1"));
            Directory.CreateDirectory(Path.Combine(src, "cache2"));
            File.WriteAllText(Path.Combine(src, "places.sqlite"), "P");
            File.WriteAllText(Path.Combine(src, "prefs.js"), "J");                       // not included
            File.WriteAllText(Path.Combine(src, "extensions", "ext1", "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(src, "cache2", "blob"), "C");                  // not included

            string dst = Path.Combine(root, "out");
            new CopyAdapter().Copy(new CopyAction
            {
                Source = src,
                Destination = dst,
                Include = new[] { "places.sqlite", "extensions/**" },
                Description = "c",
                Reason = "t",
            });

            Assert.True(File.Exists(Path.Combine(dst, "places.sqlite")));
            Assert.True(File.Exists(Path.Combine(dst, "extensions", "ext1", "manifest.json")));
            Assert.False(File.Exists(Path.Combine(dst, "prefs.js")));        // excluded by the allow-list
            Assert.False(File.Exists(Path.Combine(dst, "cache2", "blob")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Skips_a_file_symlink_aliasing_a_secret_store()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "profile");
            Directory.CreateDirectory(src);
            string secret = Path.Combine(root, "real-secret");
            File.WriteAllText(secret, "PASSWORDS");
            File.WriteAllText(Path.Combine(src, "Bookmarks"), "{}");

            string link = Path.Combine(src, "InnocentName");
            try { File.CreateSymbolicLink(link, secret); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }

            string dst = Path.Combine(root, "out");
            new CopyAdapter().Copy(new CopyAction { Source = src, Destination = dst, Description = "c", Reason = "t" });

            Assert.True(File.Exists(Path.Combine(dst, "Bookmarks")));
            Assert.False(File.Exists(Path.Combine(dst, "InnocentName"))); // file reparse point skipped
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Throws_when_the_copy_source_is_missing()
    {
        string root = TempDir();
        try
        {
            Assert.Throws<FileNotFoundException>(() => new CopyAdapter().Copy(new CopyAction
            {
                Source = Path.Combine(root, "nope.txt"),
                Destination = Path.Combine(root, "out.txt"),
                Description = "c",
                Reason = "t",
            }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // F1: write-boundary destination TOCTOU re-check (the write-side counterpart of the delete adapter's
    // pre-op reparse re-check). A copy whose destination PARENT is a junction is refused with the typed
    // DestinationReparseException, so a same-user attacker cannot swap a destination parent → junction (into a
    // protected tree) between gate-authorize and the copy. Uses a real-FS junction in temp; if a junction
    // cannot be created in this environment the test self-skips (mirrors the symlink-alias test pattern).
    [Fact]
    public void Refuses_a_single_file_copy_whose_destination_parent_is_a_junction()
    {
        string root = TempDir();
        string junctionParent = Path.Combine(root, "junctionParent");
        try
        {
            string src = Path.Combine(root, "a.txt");
            File.WriteAllText(src, "hello");

            string realTarget = Path.Combine(root, "realTarget");
            Directory.CreateDirectory(realTarget);
            if (!JunctionHelper.TryCreateJunction(junctionParent, realTarget))
                return; // junction creation unavailable here → skip (the guard is still unit-tested below)

            // Destination is UNDER the junction parent → the write boundary must refuse it.
            string dest = Path.Combine(junctionParent, "a.txt");

            Assert.Throws<DestinationReparseException>(() => new CopyAdapter().Copy(new CopyAction
            {
                Source = src,
                Destination = dest,
                Description = "copy",
                Reason = "t",
            }));

            // The destructive write never happened: nothing was dropped through the junction into the real target.
            Assert.False(File.Exists(Path.Combine(realTarget, "a.txt")));
        }
        finally { JunctionHelper.CleanupWithJunction(root, junctionParent); }
    }

    [Fact]
    public void Refuses_a_tree_copy_whose_destination_root_is_a_junction()
    {
        string root = TempDir();
        string junctionDest = Path.Combine(root, "junctionDest");
        try
        {
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "top.txt"), "1");

            string realTarget = Path.Combine(root, "realTarget");
            Directory.CreateDirectory(realTarget);
            if (!JunctionHelper.TryCreateJunction(junctionDest, realTarget))
                return; // junction creation unavailable → skip

            Assert.Throws<DestinationReparseException>(() => new CopyAdapter().Copy(new CopyAction
            {
                Source = src,
                Destination = junctionDest, // the destination root itself is the junction
                Description = "copy",
                Reason = "t",
            }));

            Assert.False(File.Exists(Path.Combine(realTarget, "top.txt")));
        }
        finally { JunctionHelper.CleanupWithJunction(root, junctionDest); }
    }

    [Fact]
    public void Refuses_a_merge_whose_destination_parent_is_a_junction()
    {
        string root = TempDir();
        string junctionParent = Path.Combine(root, "junctionParent");
        try
        {
            string src = Path.Combine(root, "new.cfg");
            File.WriteAllText(src, "NEW");

            string realTarget = Path.Combine(root, "realTarget");
            Directory.CreateDirectory(realTarget);
            if (!JunctionHelper.TryCreateJunction(junctionParent, realTarget))
                return; // junction creation unavailable → skip

            string dest = Path.Combine(junctionParent, "live.cfg");

            Assert.Throws<DestinationReparseException>(() => new CopyAdapter().Merge(new RestoreMergeAction
            {
                Source = src,
                Destination = dest,
                CreateBak = true,
                Description = "merge",
                Reason = "t",
            }));

            Assert.False(File.Exists(Path.Combine(realTarget, "live.cfg")));
        }
        finally { JunctionHelper.CleanupWithJunction(root, junctionParent); }
    }
}

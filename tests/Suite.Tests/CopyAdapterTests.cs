using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Modules.Backup;
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
        finally { TestFs.DeleteResilient(root); }
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
        finally { TestFs.DeleteResilient(root); }
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
        finally { TestFs.DeleteResilient(root); }
    }

    [Fact]
    public void Merge_with_BakPath_backs_up_to_the_exact_requested_path()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "new.cfg");
            string dst = Path.Combine(root, "live.cfg");
            string bak = Path.Combine(root, "live.cfg.bak.entry.run");
            File.WriteAllText(src, "NEW");
            File.WriteAllText(dst, "OLD");

            new CopyAdapter().Merge(new RestoreMergeAction
            {
                Source = src,
                Destination = dst,
                BakPath = bak,
                Description = "merge",
                Reason = "t",
            });

            Assert.Equal("NEW", File.ReadAllText(dst));
            Assert.Equal("OLD", File.ReadAllText(bak));
            Assert.Equal(new[] { bak }, Directory.GetFiles(root, "live.cfg.bak.*"));
        }
        finally { TestFs.DeleteResilient(root); }
    }

    [Fact]
    public void Merge_with_BakPath_collision_throws_without_random_fallback()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "new.cfg");
            string dst = Path.Combine(root, "live.cfg");
            string bak = Path.Combine(root, "live.cfg.bak.entry.run");
            File.WriteAllText(src, "NEW");
            File.WriteAllText(dst, "OLD");
            File.WriteAllText(bak, "EXISTING");

            Assert.Throws<IOException>(() => new CopyAdapter().Merge(new RestoreMergeAction
            {
                Source = src,
                Destination = dst,
                BakPath = bak,
                Description = "merge",
                Reason = "t",
            }));

            Assert.Equal("OLD", File.ReadAllText(dst));
            Assert.Equal("EXISTING", File.ReadAllText(bak));
            Assert.Equal(new[] { bak }, Directory.GetFiles(root, "live.cfg.bak.*"));
        }
        finally { TestFs.DeleteResilient(root); }
    }

    [Fact]
    public void Merge_with_non_sibling_BakPath_throws()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "new.cfg");
            string dst = Path.Combine(root, "live.cfg");
            string other = Path.Combine(root, "other");
            Directory.CreateDirectory(other);
            File.WriteAllText(src, "NEW");
            File.WriteAllText(dst, "OLD");

            Assert.Throws<ArgumentException>(() => new CopyAdapter().Merge(new RestoreMergeAction
            {
                Source = src,
                Destination = dst,
                BakPath = Path.Combine(other, "live.cfg.bak.entry.run"),
                Description = "merge",
                Reason = "t",
            }));

            Assert.Equal("OLD", File.ReadAllText(dst));
        }
        finally { TestFs.DeleteResilient(root); }
    }

    [Fact]
    public void BakPath_does_not_change_restore_merge_signature_or_plan_hash()
    {
        var a = new RestoreMergeAction
        {
            Source = @"C:\Users\bob\pkg\a.cfg",
            Destination = @"C:\Users\bob\a.cfg",
            BakPath = @"C:\Users\bob\a.cfg.bak.one",
            Description = "merge",
            Reason = "t",
        };
        var b = a with { BakPath = @"C:\Users\bob\a.cfg.bak.two" };

        Assert.Equal(a.TargetSignature(), b.TargetSignature());

        var p1 = new OperationPlan("restore", "migration-restore", new[] { a }, DateTime.UnixEpoch);
        var p2 = new OperationPlan("restore", "migration-restore", new[] { b }, DateTime.UnixEpoch.AddDays(1));
        Assert.Equal(p1.ComputeHash(), p2.ComputeHash());
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
        finally { TestFs.DeleteResilient(root); }
    }

    [Fact]
    public void Refuses_to_copy_a_known_secret_store()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "Login Data");
            File.WriteAllText(src, "secret");

            CopyAdapterResult result = new CopyAdapter().Copy(new CopyAction
            {
                Source = src,
                Destination = Path.Combine(root, "out"),
                Description = "copy",
                Reason = "t",
            });

            CopySkippedItem skip = Assert.Single(result.Skipped);
            Assert.False(result.CopiedAny);
            Assert.Equal(CopySkipReason.ExcludedByName, skip.Reason);
            Assert.False(File.Exists(Path.Combine(root, "out")));
        }
        finally { TestFs.DeleteResilient(root); }
    }

    [Fact]
    public void Skips_text_file_with_embedded_token_before_copying_bytes()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "profile");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "settings.json"),
                "{ \"apiKey\": \"synthetic-value-for-copy-scan\" }");
            File.WriteAllText(Path.Combine(src, "keybindings.json"), "{}");

            string dst = Path.Combine(root, "out");
            CopyAdapterResult result = new CopyAdapter().Copy(new CopyAction
            {
                Source = src,
                Destination = dst,
                Description = "copy",
                Reason = "t",
            });

            Assert.True(File.Exists(Path.Combine(dst, "keybindings.json")), "benign text file should be copied");
            Assert.False(File.Exists(Path.Combine(dst, "settings.json")), "settings.json with embedded token must be dropped");
            CopySkippedItem skip = Assert.Single(result.Skipped);
            Assert.Equal(CopySkipReason.ExcludedEmbeddedSecret, skip.Reason);
            Assert.Contains("key/value", skip.Detail);
            Assert.True(result.CopiedAny);
        }
        finally { TestFs.DeleteResilient(root); }
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
        finally { TestFs.DeleteResilient(root); }
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
        finally { TestFs.DeleteResilient(root); }
    }

    [FactRequiresSymlink]
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
            File.CreateSymbolicLink(link, secret);

            string dst = Path.Combine(root, "out");
            new CopyAdapter().Copy(new CopyAction { Source = src, Destination = dst, Description = "c", Reason = "t" });

            Assert.True(File.Exists(Path.Combine(dst, "Bookmarks")));
            Assert.False(File.Exists(Path.Combine(dst, "InnocentName"))); // file reparse point skipped
        }
        finally { TestFs.DeleteResilient(root); }
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
        finally { TestFs.DeleteResilient(root); }
    }

    // F1: write-boundary destination TOCTOU re-check (the write-side counterpart of the delete adapter's
    // pre-op reparse re-check). A copy whose destination PARENT is a junction is refused with the typed
    // DestinationReparseException, so a same-user attacker cannot swap a destination parent → junction (into a
    // protected tree) between gate-authorize and the copy. Uses a real-FS junction in temp; statically skipped
    // (not silently passed) when a junction cannot be created in this environment.
    [FactRequiresJunction]
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
            Assert.True(JunctionHelper.TryCreateJunction(junctionParent, realTarget)); // gated by [FactRequiresJunction]

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

    [FactRequiresJunction]
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
            Assert.True(JunctionHelper.TryCreateJunction(junctionDest, realTarget)); // gated by [FactRequiresJunction]

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

    [FactRequiresJunction]
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
            Assert.True(JunctionHelper.TryCreateJunction(junctionParent, realTarget)); // gated by [FactRequiresJunction]

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

    // ---- Audit Item 2: collision-proof .bak (two same-destination merges must not lose the original) ----
    // Old code: .bak name = dest + ".bak." + yyyyMMdd_HHmmss AND the copy used overwrite:true. Two restore
    // merges to the SAME destination within the same second produced the SAME .bak name → the second backup
    // overwrote the first → the user's ORIGINAL content was destroyed. The fix uses a high-res stamp + Guid
    // and FileMode.CreateNew so the original is always recoverable from SOME .bak.

    [Fact]
    public void Two_merges_to_the_same_destination_keep_the_original_in_a_bak()
    {
        string root = TempDir();
        try
        {
            string dst = Path.Combine(root, "live.cfg");
            string srcA = Path.Combine(root, "A.cfg");
            string srcB = Path.Combine(root, "B.cfg");
            File.WriteAllText(dst, "ORIGINAL"); // O — must survive
            File.WriteAllText(srcA, "AAA");
            File.WriteAllText(srcB, "BBB");

            var adapter = new CopyAdapter();
            // First restore merge: backs up O, then writes A.
            adapter.Merge(new RestoreMergeAction
            {
                Source = srcA, Destination = dst, CreateBak = true, Description = "m1", Reason = "t",
            });
            // Second restore merge to the SAME destination (the collision case): backs up A, then writes B.
            adapter.Merge(new RestoreMergeAction
            {
                Source = srcB, Destination = dst, CreateBak = true, Description = "m2", Reason = "t",
            });

            Assert.Equal("BBB", File.ReadAllText(dst)); // destination ends with the last restore

            // The ORIGINAL content must be recoverable from SOME .bak (it was not clobbered by the 2nd backup).
            string[] baks = Directory.GetFiles(root, "live.cfg.bak.*");
            Assert.Contains(baks, b => File.ReadAllText(b) == "ORIGINAL");
            // And the two backups are distinct files (no name collision): O and A both preserved.
            Assert.Contains(baks, b => File.ReadAllText(b) == "AAA");
            Assert.True(baks.Length >= 2, "each merge must produce its own distinct .bak");
        }
        finally { TestFs.DeleteResilient(root); }
    }

    // ---- Audit Item 3: a hard link under a benign leaf aliases a secret; GetFinalPathNameByHandle cannot
    // de-alias it, so any multi-linked file is refused. Real-FS hard link in temp; statically skipped (not
    // silently passed) when CreateHardLink is unavailable on this volume.

    [FactRequiresHardlink]
    public void Skips_a_hardlink_aliasing_a_secret_store()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "profile");
            Directory.CreateDirectory(src);

            // A "secret" target file (filename-based, innocuous content — no real secret literal).
            string secret = Path.Combine(root, "real-secret");
            File.WriteAllText(secret, "x");
            File.WriteAllText(Path.Combine(src, "Bookmarks"), "{}");

            // A hard link under a totally benign leaf name aliasing the secret, INSIDE the copy source tree.
            string link = Path.Combine(src, "settings.json");
            Assert.True(HardLinkInterop.TryCreateHardLink(link, secret)); // gated by [FactRequiresHardlink]

            string dst = Path.Combine(root, "out");
            new CopyAdapter().Copy(new CopyAction { Source = src, Destination = dst, Description = "c", Reason = "t" });

            Assert.True(File.Exists(Path.Combine(dst, "Bookmarks")));         // normal file copied
            Assert.False(File.Exists(Path.Combine(dst, "settings.json")));    // multi-linked alias refused
        }
        finally { TestFs.DeleteResilient(root); }
    }

    [FactRequiresHardlink]
    public void Refuses_a_single_file_copy_of_a_hardlink()
    {
        string root = TempDir();
        try
        {
            string secret = Path.Combine(root, "real-secret");
            File.WriteAllText(secret, "x");
            string link = Path.Combine(root, "notes.db");
            Assert.True(HardLinkInterop.TryCreateHardLink(link, secret));

            string dst = Path.Combine(root, "out", "notes.db");
            CopyAdapterResult result =
                new CopyAdapter().Copy(new CopyAction { Source = link, Destination = dst, Description = "c", Reason = "t" });

            Assert.False(File.Exists(dst));
            CopySkippedItem skip = Assert.Single(result.Skipped);
            Assert.Equal(CopySkipReason.HardLinked, skip.Reason);
            Assert.False(result.CopiedAny);
        }
        finally { TestFs.DeleteResilient(root); }
    }

    [Fact]
    public void Copies_a_normal_single_linked_file_after_the_hardlink_guard()
    {
        // Positive counter-test: the hard-link guard must NOT refuse an ordinary file (nNumberOfLinks == 1).
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "ordinary.txt");
            string dst = Path.Combine(root, "out", "ordinary.txt");
            File.WriteAllText(src, "hello");

            new CopyAdapter().Copy(new CopyAction { Source = src, Destination = dst, Description = "c", Reason = "t" });

            Assert.Equal("hello", File.ReadAllText(dst));
        }
        finally { TestFs.DeleteResilient(root); }
    }

    // ---- Audit Item 5: true "**" semantics — "**" matches across path separators while single "*" stays
    // within one segment. Old code translated "**" to "[^/]*[^/]*" (still within-segment) so a shipped recipe
    // include like ["**/memory/**","**/*.md"] silently dropped nested notes/memory trees (data loss).

    [Fact]
    public void Double_star_include_matches_across_separators()
    {
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(Path.Combine(src, "a", "b", "memory"));
            Directory.CreateDirectory(Path.Combine(src, "a", "b", "c"));
            File.WriteAllText(Path.Combine(src, "a", "b", "memory", "x.dat"), "M"); // matched by **/memory/**
            File.WriteAllText(Path.Combine(src, "a", "b", "c", "note.md"), "N");     // matched by **/*.md

            string dst = Path.Combine(root, "out");
            new CopyAdapter().Copy(new CopyAction
            {
                Source = src,
                Destination = dst,
                Include = new[] { "**/memory/**", "**/*.md" },
                Description = "c",
                Reason = "t",
            });

            Assert.True(File.Exists(Path.Combine(dst, "a", "b", "memory", "x.dat")), "deep memory/ file must be copied");
            Assert.True(File.Exists(Path.Combine(dst, "a", "b", "c", "note.md")), "deep .md note must be copied");
        }
        finally { TestFs.DeleteResilient(root); }
    }

    [Fact]
    public void Single_star_include_stays_within_one_segment()
    {
        // Guards the OTHER half of Item 5: a single "*" must NOT cross a separator. "a/*.md" matches a
        // top-level a/note.md but NOT a/sub/deep.md (that would need "a/**/*.md").
        string root = TempDir();
        try
        {
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(Path.Combine(src, "a", "sub"));
            File.WriteAllText(Path.Combine(src, "a", "note.md"), "T");
            File.WriteAllText(Path.Combine(src, "a", "sub", "deep.md"), "D");

            string dst = Path.Combine(root, "out");
            new CopyAdapter().Copy(new CopyAction
            {
                Source = src,
                Destination = dst,
                Include = new[] { "a/*.md" },
                Description = "c",
                Reason = "t",
            });

            Assert.True(File.Exists(Path.Combine(dst, "a", "note.md")));        // within-segment match
            Assert.False(File.Exists(Path.Combine(dst, "a", "sub", "deep.md"))); // single * did not cross '/'
        }
        finally { TestFs.DeleteResilient(root); }
    }
}

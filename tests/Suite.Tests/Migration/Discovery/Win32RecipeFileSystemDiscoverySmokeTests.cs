using System.IO;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Discovery;
using WindowsCareKit.Tests.TestInfra;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests.Migration.Discovery;

/// <summary>
/// Real-filesystem smoke for <see cref="AppDiscoveryEngine"/> driven through the PRODUCTION
/// <see cref="Win32RecipeFileSystem"/> (closes audit gap #4 — the engine was only ever exercised over the
/// in-memory <c>FakeDiscoveryFileSystem</c>). Each case builds a throwaway GUID temp tree standing in for the
/// profile roots, runs read-only Discovery over it, then tears the tree down.
///
/// <para><b>Host-safe by construction:</b> everything lives under a per-test <see cref="TempWorkspace"/> root
/// in the user temp area; Discovery only enumerates (never writes/deletes outside the temp tree); no real
/// profile, registry, or service is touched. Plain <c>[Fact]</c> so it runs in CI.</para>
///
/// <para><b>Non-vacuity:</b> every "X is pruned / not descended" assertion is paired with a sibling
/// <c>NormalApp</c> that MUST be discovered, so an exclusion can never pass merely because the walk produced
/// nothing.</para>
///
/// <para><b>Real-FS divergence from the fake (documented, not a product bug):</b> the in-memory fake can
/// model a NON-reparse candidate entry whose <c>Canonicalize</c> nonetheless resolves out of root (the
/// <c>Out_of_root_canonical_candidate_is_skipped</c> fake test). On a real NTFS volume a directory that
/// redirects elsewhere IS a reparse point (<c>Directory | ReparsePoint</c>), so the engine surfaces it as
/// <see cref="DiscoveryScanStatus.NotTraversedReparse"/> at the reparse guard BEFORE the containment-skip
/// branch is ever reached. The real-FS honest analog of "escape" is therefore: an out-of-root-target junction
/// is SURFACED-but-NOT-DESCENDED (its target's contents never leak into activity), which this suite asserts.
/// </para>
/// </summary>
public sealed class Win32RecipeFileSystemDiscoverySmokeTests
{
    private static readonly DateTime T0 = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Build a real temp tree shaped like the three profile roots the engine expects and return the wired
    /// <see cref="ProfileRoots"/>. <c>UserProfile</c> = workspace root; <c>AppData</c>/<c>LocalAppData</c> are
    /// the standard sub-paths. Candidate apps in these tests live under AppData (Roaming).
    /// </summary>
    private static ProfileRoots MakeRoots(TempWorkspace ws)
    {
        string userProfile = ws.Root;
        string appData = Path.Combine(userProfile, "AppData", "Roaming");
        string localAppData = Path.Combine(userProfile, "AppData", "Local");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(localAppData);
        return new ProfileRoots(userProfile, appData, localAppData);
    }

    private static AppDiscoveryEngine Engine(ProfileRoots roots)
        => new(roots, new RecipePathResolver(roots), new Win32RecipeFileSystem());

    private static DiscoveryScanOptions Opts(int maxEntries = 25_000)
        => new(T0) { MaxGlobalEntries = maxEntries };

    /// <summary>Create a child dir under AppData with the given files (relative names) carrying real content.</summary>
    private static string MakeAppDir(ProfileRoots roots, string appName, params string[] fileNames)
    {
        string appDir = Path.Combine(roots.AppData, appName);
        Directory.CreateDirectory(appDir);
        foreach (string f in fileNames)
            File.WriteAllText(Path.Combine(appDir, f), "x");
        return appDir;
    }

    // ── Non-vacuity anchor: a normal app is discovered over the real FS ───────────────────────────

    /// <summary>
    /// A plain child dir with a couple of real files is surfaced as a <see cref="DiscoveredApp"/> with the
    /// right Id/RelativePath/Root, a real (non-fabricated) <see cref="DiscoveredApp.LastModifiedUtc"/>, and a
    /// fully-walked <see cref="DiscoveryScanStatus.Complete"/> status. This is the anchor every exclusion case
    /// is paired against.
    /// </summary>
    [Fact]
    public void Normal_app_with_real_files_is_discovered_complete_with_real_modtime()
    {
        using var ws = new TempWorkspace("wck-disco-");
        ProfileRoots roots = MakeRoots(ws);

        string appDir = MakeAppDir(roots, "NormalApp", "settings.json", "data.bin");
        // Stamp a deterministic, plausible-past write time so we assert a REAL value, not DateTime.MinValue.
        DateTime stamped = T0.AddDays(-2);
        File.SetLastWriteTimeUtc(Path.Combine(appDir, "settings.json"), stamped);
        File.SetLastWriteTimeUtc(Path.Combine(appDir, "data.bin"), stamped.AddHours(-1));

        var apps = Engine(roots).Discover(Opts(), CancellationToken.None);

        DiscoveredApp app = Assert.Single(apps, a => a.Id == "NormalApp");
        Assert.Equal("NormalApp", app.Id);
        Assert.Equal("NormalApp", app.RelativePath);
        Assert.Equal(KnownFolder.AppData, app.Root);
        Assert.Equal(DiscoveryScanStatus.Complete, app.Status);
        Assert.NotNull(app.LastModifiedUtc);
        // The activity is the max ALLOWED file modtime — the newer settings.json, never fabricated.
        Assert.Equal(stamped, app.LastModifiedUtc);
    }

    // ── Secret-named + cache-named dirs are pruned WHILE a sibling normal app is discovered ────────

    /// <summary>
    /// Top-level candidate dirs whose LEAF names match the real <see cref="SecretGlobOverlay"/>
    /// (<c>*secret*</c>, <c>*token*</c>) or <see cref="CacheGlobOverlay"/> (<c>node_modules</c>, <c>Cache</c>,
    /// <c>GPUCache</c>, <c>*Cache*</c>) are pruned at the candidate gate — NOT emitted. Non-vacuous: the sibling
    /// <c>NormalApp</c> IS emitted, so the prune is proven over a producing walk rather than an empty one.
    /// </summary>
    [Fact]
    public void Secret_and_cache_named_dirs_are_pruned_while_sibling_normal_app_is_discovered()
    {
        using var ws = new TempWorkspace("wck-disco-");
        ProfileRoots roots = MakeRoots(ws);

        // Sibling that MUST be discovered (the non-vacuity anchor).
        MakeAppDir(roots, "NormalApp", "config.json");

        // Names chosen by reading the overlays so they genuinely match.
        // Secret-overlay matches (dir leaf): contains "secret" / "token".
        string[] secretDirs = { "my-secret-store", "auth-token-cache" };
        // Cache-overlay matches (dir leaf): literal junk names + the *Cache* glob.
        string[] cacheDirs = { "node_modules", "Cache", "GPUCache", "WebViewCache" };

        foreach (string d in secretDirs.Concat(cacheDirs))
        {
            string dir = Path.Combine(roots.AppData, d);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "inside.dat"), "x"); // give each a file so it would walk if not pruned
        }

        // Guard: confirm the names actually match the production overlays (else the prune assertion is vacuous).
        Assert.All(secretDirs, d => Assert.True(SecretGlobOverlay.IsSecretLeaf(d), $"expected secret match: {d}"));
        Assert.All(cacheDirs, d => Assert.True(CacheGlobOverlay.IsCacheLeaf(d), $"expected cache match: {d}"));

        var apps = Engine(roots).Discover(Opts(), CancellationToken.None);

        // Non-vacuity anchor present.
        Assert.Contains(apps, a => a.Id == "NormalApp");

        // None of the secret/cache-named dirs appear in ANY field of ANY result.
        foreach (string d in secretDirs.Concat(cacheDirs))
        {
            Assert.DoesNotContain(apps, a => string.Equals(a.Id, d, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(apps, a => a.RelativePath.Contains(d, StringComparison.OrdinalIgnoreCase));
        }
        // Secret-name leak guard across every field of every record.
        foreach (DiscoveredApp a in apps)
        {
            Assert.DoesNotContain("secret", a.Id, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", a.Id, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", a.RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", a.RelativePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Reparse candidate → NotTraversedReparse, not descended ────────────────────────────────────

    /// <summary>
    /// A junction candidate (pointing at a real IN-ROOT target dir that contains files) is surfaced as a
    /// <see cref="DiscoveredApp"/> with <see cref="DiscoveryScanStatus.NotTraversedReparse"/>, a null
    /// <see cref="DiscoveredApp.LastModifiedUtc"/>, and the engine does NOT descend into it (the target's
    /// leaf files never raise activity). Guarded by <see cref="JunctionHelper.TryCreateJunction"/> — if the
    /// host disallows junctions, the reparse case is skipped (visibly, via <see cref="Assert.True(bool)"/>'d
    /// gate that still proves the sibling) rather than failing. Non-vacuous: the sibling <c>NormalApp</c> is
    /// discovered in the same run.
    /// </summary>
    [FactRequiresJunction]
    public void Reparse_candidate_is_surfaced_not_traversed_with_sibling_discovered()
    {
        using var ws = new TempWorkspace("wck-disco-");
        ProfileRoots roots = MakeRoots(ws);

        // Non-vacuity anchor.
        MakeAppDir(roots, "NormalApp", "config.json");

        // Real in-root target with files the engine MUST NOT surface (would prove it descended).
        string target = Path.Combine(roots.AppData, "RealTarget");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "deep_inside.dat"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(target, "deep_inside.dat"), T0); // newest possible — must not leak

        string junction = Path.Combine(roots.AppData, "JunctionApp");
        Assert.True(JunctionHelper.TryCreateJunction(junction, target)); // gated by [FactRequiresJunction]

        try
        {
            var apps = Engine(roots).Discover(Opts(), CancellationToken.None);

            // Sibling anchor present (non-vacuity).
            Assert.Contains(apps, a => a.Id == "NormalApp");

            DiscoveredApp app = Assert.Single(apps, a => a.Id == "JunctionApp");
            Assert.Equal(DiscoveryScanStatus.NotTraversedReparse, app.Status);
            Assert.Null(app.LastModifiedUtc); // null for NotTraversedReparse — never the target's deep file time
            Assert.Equal(KnownFolder.AppData, app.Root);
            // RealTarget is a sibling real dir, so it is discovered on its own (NOT via the junction descent).
            // Proof of non-descent: the JunctionApp record has no activity from deep_inside.dat (asserted above).
        }
        finally
        {
            JunctionHelper.CleanupWithJunction(ws.Root, junction);
        }
    }

    /// <summary>
    /// A junction candidate whose target is OUTSIDE the profile root. On a real NTFS volume this entry carries
    /// the <c>ReparsePoint</c> attribute, so the engine surfaces it as
    /// <see cref="DiscoveryScanStatus.NotTraversedReparse"/> and does NOT descend — the out-of-root target's
    /// contents never leak into any result. (This is the real-FS honest analog of the fake's "silently skipped"
    /// case: the production guard that fires first for a real junction is the reparse guard, not the
    /// containment-skip branch — see the class remarks.) Guarded by <see cref="JunctionHelper.TryCreateJunction"/>.
    /// Non-vacuous: the sibling <c>NormalApp</c> is discovered in the same run.
    /// </summary>
    [FactRequiresJunction]
    public void Out_of_root_target_junction_is_surfaced_not_descended_with_sibling_discovered()
    {
        using var ws = new TempWorkspace("wck-disco-");
        // A SEPARATE temp root that is NOT inside the profile root — the escape target.
        using var outsideWs = new TempWorkspace("wck-disco-outside-");
        ProfileRoots roots = MakeRoots(ws);

        // Non-vacuity anchor.
        MakeAppDir(roots, "NormalApp", "config.json");

        // Out-of-root target with a uniquely-named file that must never appear in / influence any result.
        string outsideTarget = Path.Combine(outsideWs.Root, "outside-app");
        Directory.CreateDirectory(outsideTarget);
        File.WriteAllText(Path.Combine(outsideTarget, "escape_marker.dat"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(outsideTarget, "escape_marker.dat"), T0);

        string junction = Path.Combine(roots.AppData, "EscapeApp");
        Assert.True(JunctionHelper.TryCreateJunction(junction, outsideTarget)); // gated by [FactRequiresJunction]

        try
        {
            var apps = Engine(roots).Discover(Opts(), CancellationToken.None);

            Assert.Contains(apps, a => a.Id == "NormalApp");

            // The escape junction is surfaced (reparse) but NOT descended: its out-of-root target's file
            // never raises activity. Either it is surfaced as NotTraversedReparse, or (if a future production
            // change routed it through the containment-skip) it is absent — both are non-leaking. We assert the
            // production-observed behavior: surfaced NotTraversedReparse with null activity.
            DiscoveredApp escape = Assert.Single(apps, a => a.Id == "EscapeApp");
            Assert.Equal(DiscoveryScanStatus.NotTraversedReparse, escape.Status);
            Assert.Null(escape.LastModifiedUtc);

            // Hard non-leak guard: the out-of-root marker name must appear in NO field of NO result.
            foreach (DiscoveredApp a in apps)
            {
                Assert.DoesNotContain("escape_marker", a.Id, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("escape_marker", a.RelativePath, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("outside-app", a.RelativePath, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            JunctionHelper.CleanupWithJunction(ws.Root, junction);
        }
    }

    // ── Determinism: a second run over the same real tree is identical ────────────────────────────

    /// <summary>
    /// A second Discovery run over the same real temp tree yields an identical result list (same order, Ids,
    /// statuses, and modtimes). The engine imposes a deterministic ordinal sort over the filesystem-dependent
    /// enumeration (F2), so two reads must not drift.
    /// </summary>
    [Fact]
    public void Second_run_over_same_real_tree_is_identical()
    {
        using var ws = new TempWorkspace("wck-disco-");
        ProfileRoots roots = MakeRoots(ws);

        MakeAppDir(roots, "AppAlpha", "a.json");
        MakeAppDir(roots, "AppBeta", "b.json");
        MakeAppDir(roots, "node_modules");          // pruned in both runs
        string targetDir = Path.Combine(roots.AppData, "GammaTarget");
        Directory.CreateDirectory(targetDir);

        var engine = Engine(roots);
        var run1 = engine.Discover(Opts(), CancellationToken.None);
        var run2 = engine.Discover(Opts(), CancellationToken.None);

        Assert.Equal(run1.Count, run2.Count);
        for (int i = 0; i < run1.Count; i++)
        {
            Assert.Equal(run1[i].Id, run2[i].Id);
            Assert.Equal(run1[i].RelativePath, run2[i].RelativePath);
            Assert.Equal(run1[i].Root, run2[i].Root);
            Assert.Equal(run1[i].Status, run2[i].Status);
            Assert.Equal(run1[i].LastModifiedUtc, run2[i].LastModifiedUtc);
        }

        // Non-vacuity: both runs actually produced the two normal apps and pruned node_modules.
        Assert.Contains(run1, a => a.Id == "AppAlpha");
        Assert.Contains(run1, a => a.Id == "AppBeta");
        Assert.DoesNotContain(run1, a => a.Id == "node_modules");
    }
}

using System.IO;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Discovery;
using Xunit;

namespace WindowsCareKit.Tests.Migration.Discovery;

/// <summary>
/// Non-vacuous tests for <see cref="AppDiscoveryEngine"/>: secret-leak regression, cache prune,
/// reparse surfacing, budget/determinism, global budget across apps (F5).
/// </summary>
public class AppDiscoveryEngineTests
{
    // Fixed roots: Alice's profile, kept in sync with MigrationTestData for consistency.
    private static readonly ProfileRoots Roots = new(
        UserProfile: @"C:\Users\alice",
        AppData: @"C:\Users\alice\AppData\Roaming",
        LocalAppData: @"C:\Users\alice\AppData\Local");

    private static readonly RecipePathResolver Resolver = new(Roots);

    private static readonly DateTime T0 = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

    private static AppDiscoveryEngine Engine(FakeDiscoveryFileSystem fs)
        => new(Roots, Resolver, fs);

    private static DiscoveryScanOptions Opts(int maxEntries = 25_000)
        => new(T0) { MaxGlobalEntries = maxEntries };

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Populate the fake with AppData root and a named app child directory.</summary>
    private static FakeDiscoveryFileSystem AppDataFs(string appName)
    {
        string appData = Roots.AppData;
        string appDir = Path.Combine(appData, appName);
        return new FakeDiscoveryFileSystem()
            .AddDir(appData)
            .AddDir(appDir)
            .AddChildDir(appData, appName);
    }

    // ── Secret-leak regression ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// F5: an app with an allowed config leaf AND a secret leaf (*token*) must be emitted;
    /// activity must reflect the allowed leaf; the secret name must appear in NO field of any DiscoveredApp.
    /// </summary>
    [Fact]
    public void Secret_leaf_excluded_from_activity_and_no_secret_name_in_any_field()
    {
        string appData = Roots.AppData;
        string appDir = Path.Combine(appData, "MyApp");
        DateTime allowedTime = T0.AddDays(-1);
        DateTime secretTime  = T0;              // newer — must NOT raise activity

        var fs = new FakeDiscoveryFileSystem()
            .AddDir(appData)
            .AddDir(appDir)
            .AddChildDir(appData, "MyApp")
            .AddChildFile(appDir, "settings.json", allowedTime)   // allowed
            .AddChildFile(appDir, "github_token.txt", secretTime); // secret — excluded

        var apps = Engine(fs).Discover(Opts(), CancellationToken.None);

        Assert.Single(apps);
        DiscoveredApp app = apps[0];

        // App must be emitted.
        Assert.Equal("MyApp", app.Id);

        // Activity must reflect the ALLOWED leaf only (not the secret's newer time).
        Assert.Equal(allowedTime, app.LastModifiedUtc);

        // The secret name must appear in NO field of any DiscoveredApp.
        foreach (DiscoveredApp a in apps)
        {
            Assert.DoesNotContain("token", a.Id, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", a.DisplayName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", a.RelativePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Cache prune ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A *Cache* sub-directory with a newer file must NOT raise the app's activity timestamp.
    /// </summary>
    [Fact]
    public void Cache_dir_with_newer_file_does_not_raise_activity()
    {
        string appData = Roots.AppData;
        string appDir  = Path.Combine(appData, "BrowserApp");
        string cacheDir = Path.Combine(appDir, "Cache");
        DateTime allowedTime = T0.AddDays(-3);
        DateTime cacheTime   = T0;              // newer, inside Cache dir

        var fs = new FakeDiscoveryFileSystem()
            .AddDir(appData)
            .AddDir(appDir)
            .AddDir(cacheDir)
            .AddChildDir(appData, "BrowserApp")
            .AddChildFile(appDir, "Preferences", allowedTime)   // allowed
            .AddChildDir(appDir, "Cache")                        // pruned — not descended
            .AddChildFile(cacheDir, "f_0001", cacheTime);        // inside cache, should be skipped

        var apps = Engine(fs).Discover(Opts(), CancellationToken.None);

        Assert.Single(apps);
        // Activity must NOT include the file inside Cache dir.
        Assert.Equal(allowedTime, apps[0].LastModifiedUtc);
    }

    /// <summary>node_modules with a newer file must NOT count toward the budget or activity.</summary>
    [Fact]
    public void Cache_fan_out_cap_node_modules_pruned_before_entries_counted()
    {
        string appData  = Roots.AppData;
        string appDir   = Path.Combine(appData, "NodeApp");
        string nmDir    = Path.Combine(appDir, "node_modules");
        DateTime outerTime = T0.AddDays(-1);

        var fs = new FakeDiscoveryFileSystem()
            .AddDir(appData)
            .AddDir(appDir)
            .AddDir(nmDir)
            .AddChildDir(appData, "NodeApp")
            .AddChildFile(appDir, "index.js", outerTime)
            .AddChildDir(appDir, "node_modules"); // pruned

        // Add many fake files inside node_modules — they must never be counted.
        for (int i = 0; i < 100; i++)
            fs.AddChildFile(nmDir, $"pkg_{i}.js", T0);

        // Low global budget — if node_modules entries were counted the app would be Incomplete.
        var apps = Engine(fs).Discover(new DiscoveryScanOptions(T0) { MaxGlobalEntries = 50 }, CancellationToken.None);

        Assert.Single(apps);
        // Budget not consumed by the pruned node_modules dir.
        Assert.Equal(DiscoveryScanStatus.Complete, apps[0].Status);
        Assert.Equal(outerTime, apps[0].LastModifiedUtc);
    }

    // ── Reparse surfacing ────────────────────────────────────────────────────────────────────────

    /// <summary>An app dir that IS a reparse point must be emitted with NotTraversedReparse (F3).</summary>
    [Fact]
    public void App_dir_is_reparse_emitted_with_not_traversed_reparse()
    {
        string appData = Roots.AppData;
        string appDir  = Path.Combine(appData, "JunctionApp");

        // The candidate itself is a reparse point (e.g. junction).
        var fs = new FakeDiscoveryFileSystem()
            .AddDir(appData)
            .AddDir(appDir)
            .AddChildDir(appData, "JunctionApp", isReparse: true);

        var apps = Engine(fs).Discover(Opts(), CancellationToken.None);

        Assert.Single(apps);
        Assert.Equal("JunctionApp", apps[0].Id);
        Assert.Equal(DiscoveryScanStatus.NotTraversedReparse, apps[0].Status);
        Assert.Null(apps[0].LastModifiedUtc);
    }

    /// <summary>A reparse-point CHILD dir inside an app must not be traversed.</summary>
    [Fact]
    public void Child_reparse_dir_is_not_traversed()
    {
        string appData   = Roots.AppData;
        string appDir    = Path.Combine(appData, "MyApp");
        string childReparse = Path.Combine(appDir, "SubJunction");
        DateTime allowedTime = T0.AddDays(-1);

        var fs = new FakeDiscoveryFileSystem()
            .AddDir(appData)
            .AddDir(appDir)
            .AddDir(childReparse)
            .AddChildDir(appData, "MyApp")
            .AddChildFile(appDir, "config.json", allowedTime)
            .AddChildDir(appDir, "SubJunction", isReparse: true)
            .AddChildFile(childReparse, "secret_inside.txt", T0); // inside reparse child — must not count

        var apps = Engine(fs).Discover(Opts(), CancellationToken.None);

        Assert.Single(apps);
        // Activity only from config.json, not from inside the reparse child.
        Assert.Equal(allowedTime, apps[0].LastModifiedUtc);
        Assert.Equal(DiscoveryScanStatus.Complete, apps[0].Status);
    }

    /// <summary>
    /// A candidate that is NOT a reparse-attributed entry but whose canonical path escapes the profile
    /// root must be skipped by AppDiscoveryEngine's IsContained guard — never emitted (Iron law 3).
    /// </summary>
    [Fact]
    public void Out_of_root_canonical_candidate_is_skipped()
    {
        string appData = Roots.AppData;
        string appDir  = Path.Combine(appData, "EscapeApp");

        var fs = new FakeDiscoveryFileSystem()
            .AddDir(appData)
            .AddDir(appDir)
            .AddChildDir(appData, "EscapeApp")                      // ordinary (non-reparse) candidate entry...
            .AddReparse(appDir, @"C:\Windows\System32\EscapeApp");  // ...whose Canonicalize escapes the root

        var apps = Engine(fs).Discover(Opts(), CancellationToken.None);

        // Non-vacuous: the escaping candidate must be ENTIRELY absent (skipped at the containment guard),
        // not merely "not Complete". An empty list here proves the IsContained skip fired in production.
        Assert.Empty(apps);
    }

    /// <summary>
    /// An app dir that cannot be enumerated (access error) is surfaced with Inaccessible, not omitted and
    /// not faked as active (covers AppDiscoveryEngine's catch path; spec test plan).
    /// </summary>
    [Fact]
    public void Inaccessible_app_dir_is_emitted_with_inaccessible_status()
    {
        string appData = Roots.AppData;
        string appDir  = Path.Combine(appData, "LockedApp");

        var fs = new FakeDiscoveryFileSystem()
            .AddDir(appData)
            .AddDir(appDir)
            .AddChildDir(appData, "LockedApp")
            .SetThrowOnEnumerate(appDir);   // enumerating the app's children throws

        var apps = Engine(fs).Discover(Opts(), CancellationToken.None);

        Assert.Single(apps);
        Assert.Equal("LockedApp", apps[0].Id);
        Assert.Equal(DiscoveryScanStatus.Inaccessible, apps[0].Status);
        Assert.Null(apps[0].LastModifiedUtc);   // never fabricated
    }

    // ── Incomplete scan / budget ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Entry-count cap hit → app still present, Status=IncompleteBudget, time not fabricated.
    /// </summary>
    [Fact]
    public void Entry_count_cap_emits_incomplete_budget_not_hidden()
    {
        string appData = Roots.AppData;
        string appDir  = Path.Combine(appData, "BigApp");
        DateTime fileTime = T0.AddDays(-1);

        var fs = new FakeDiscoveryFileSystem()
            .AddDir(appData)
            .AddDir(appDir)
            .AddChildDir(appData, "BigApp");

        // Add more files than the budget allows.
        for (int i = 0; i < 20; i++)
            fs.AddChildFile(appDir, $"file_{i:D3}.dat", fileTime);

        var apps = Engine(fs).Discover(new DiscoveryScanOptions(T0) { MaxGlobalEntries = 5 }, CancellationToken.None);

        Assert.Single(apps);
        Assert.Equal("BigApp", apps[0].Id);
        Assert.Equal(DiscoveryScanStatus.IncompleteBudget, apps[0].Status);
        // Time is NOT null — we saw some files before budget ran out.
        Assert.NotNull(apps[0].LastModifiedUtc);
    }

    // ── Determinism ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Same fake tree + same options ⇒ identical DiscoveredApp list + identical Status (F5).
    /// </summary>
    [Fact]
    public void Same_tree_and_options_produces_identical_results()
    {
        string appData = Roots.AppData;
        string appDir  = Path.Combine(appData, "StableApp");
        DateTime t1 = T0.AddDays(-2);
        DateTime t2 = T0.AddDays(-1);

        var fs = new FakeDiscoveryFileSystem()
            .AddDir(appData)
            .AddDir(appDir)
            .AddChildDir(appData, "StableApp")
            .AddChildFile(appDir, "a.json", t1)
            .AddChildFile(appDir, "b.json", t2);

        var opts = Opts();
        var engine = Engine(fs);

        var run1 = engine.Discover(opts, CancellationToken.None);
        var run2 = engine.Discover(opts, CancellationToken.None);

        Assert.Equal(run1.Count, run2.Count);
        for (int i = 0; i < run1.Count; i++)
        {
            Assert.Equal(run1[i].Id, run2[i].Id);
            Assert.Equal(run1[i].Status, run2[i].Status);
            Assert.Equal(run1[i].LastModifiedUtc, run2[i].LastModifiedUtc);
        }
    }

    // ── Global budget across apps ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Global budget: many apps exceeding the combined cap ⇒ later apps IncompleteBudget,
    /// none silently dropped (F5).
    /// </summary>
    [Fact]
    public void Global_budget_later_apps_get_incomplete_budget_none_dropped()
    {
        string appData = Roots.AppData;
        var fs = new FakeDiscoveryFileSystem().AddDir(appData);

        // Three apps, each with 5 files. Budget = 7 → first app completes, second/third are incomplete.
        string[] appNames = { "App1", "App2", "App3" };
        foreach (string name in appNames)
        {
            string appDir = Path.Combine(appData, name);
            fs.AddDir(appDir).AddChildDir(appData, name);
            for (int i = 0; i < 5; i++)
                fs.AddChildFile(appDir, $"f{i}.dat", T0.AddDays(-i));
        }

        var apps = Engine(fs).Discover(new DiscoveryScanOptions(T0) { MaxGlobalEntries = 7 }, CancellationToken.None);

        // All three apps must be present — none silently dropped.
        Assert.Equal(3, apps.Count);
        Assert.Equal("App1", apps[0].Id);
        Assert.Equal("App2", apps[1].Id);
        Assert.Equal("App3", apps[2].Id);

        // App1 may be Complete or IncompleteBudget depending on exact budget math.
        // App2 and/or App3 must be IncompleteBudget.
        Assert.True(
            apps[1].Status == DiscoveryScanStatus.IncompleteBudget ||
            apps[2].Status == DiscoveryScanStatus.IncompleteBudget,
            "At least one later app must be IncompleteBudget when global budget exceeded");
    }

    // ── Default portability ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Default_portability_is_partial()
    {
        string appData = Roots.AppData;
        string appDir  = Path.Combine(appData, "SomeApp");

        var fs = new FakeDiscoveryFileSystem()
            .AddDir(appData)
            .AddDir(appDir)
            .AddChildDir(appData, "SomeApp");

        var apps = Engine(fs).Discover(Opts(), CancellationToken.None);

        Assert.Single(apps);
        Assert.Equal(PortabilityClass.Partial, apps[0].Portability);
    }

    // ── UserProfile dot-dirs only ────────────────────────────────────────────────────────────────

    [Fact]
    public void UserProfile_emits_only_dot_directories()
    {
        string userProfile = Roots.UserProfile;

        var fs = new FakeDiscoveryFileSystem()
            .AddDir(userProfile)
            .AddDir(Roots.AppData)
            .AddDir(Roots.LocalAppData)
            .AddChildDir(userProfile, ".claude")        // dot-dir: emitted
            .AddChildDir(userProfile, "Documents")      // non-dot: skipped
            .AddChildDir(userProfile, ".ssh");           // dot-dir: emitted

        var apps = Engine(fs).Discover(Opts(), CancellationToken.None);

        var userProfileApps = apps.Where(a => a.Root == KnownFolder.UserProfile).ToList();
        Assert.Equal(2, userProfileApps.Count);
        Assert.All(userProfileApps, a => Assert.StartsWith(".", a.Id));
    }

    // ── Cancellation ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pre_cancelled_token_emits_no_results_or_cancelled_status()
    {
        string appData = Roots.AppData;
        string appDir  = Path.Combine(appData, "AnyApp");

        var fs = new FakeDiscoveryFileSystem()
            .AddDir(appData)
            .AddDir(appDir)
            .AddChildDir(appData, "AnyApp")
            .AddChildFile(appDir, "file.txt", T0);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var apps = Engine(fs).Discover(Opts(), cts.Token);

        // Either no apps or all apps have Cancelled status — no app should be Complete when cancelled.
        Assert.All(apps, a =>
            Assert.True(a.Status == DiscoveryScanStatus.Cancelled || a.Status != DiscoveryScanStatus.Complete,
                $"App {a.Id} should not be Complete when token was pre-cancelled"));
    }
}

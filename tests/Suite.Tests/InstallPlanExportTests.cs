using System.IO;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Step 3 (Kur/Install) host-safe verification of the EXPORT vertical slice — the mirror of
/// <see cref="BackupIntegrityTests"/>. <see cref="InstallPlanExport.Build"/> is a pure, zero-IO classification
/// (in-memory plan results). The write/runner paths use only a <see cref="TempWorkspace"/> under
/// <see cref="Path.GetTempPath"/> — never a real user/profile path. NOTHING is installed, no process is
/// spawned, no elevation happens: the export reads the plan and writes JSON only.
/// </summary>
public class InstallPlanExportTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc);

    // %TEMP% lives under the real current user's profile, so the hardened write-target gate allows it.
    private static ISafetyGate RealGate()
        => new SafetyGate(ProtectedResources.ForCurrentSystem(), new FakeCanonicalizer());

    /// <summary>A driver guard that confirms every identifier as Net (so a driver entry is not skipped on class).</summary>
    private sealed class AllNetDriverGuard : IDriverGuard
    {
        public bool IsNetClass(string driverIdentifier) => true;
    }

    /// <summary>Run the REAL planner over a manifest to obtain a realistic <see cref="InstallPlanResult"/>.</summary>
    private static InstallPlanResult Plan(params InstallEntry[] entries)
        => new InstallPlanner(RealGate(), new AllNetDriverGuard())
            .BuildPlan(new InstallManifest(entries), RestoreState.Empty, T0);

    private static InstallEntry Winget(string id, string wingetId, bool admin = false, int order = 100)
        => new(id, "install", "winget", InstallMethod.Winget, wingetId, null, admin, false, order, $"Install {id}");

    private static InstallEntry Npm(string id, string pkg, int order = 200)
        => new(id, "install", "ai-cli", InstallMethod.Npm, null, pkg, false, false, order, $"npm {id}");

    private static InstallEntry Config(string id, string src, string dst, int order = 300)
        => new(id, "install", "config", InstallMethod.ConfigRestore, null, null, false, false, order, $"config {id}")
        { ConfigSource = src, ConfigDestination = dst };

    private static InstallEntry UrlManual(string id, string url, int order = 400)
        => new(id, "install", "tarayici", InstallMethod.UrlManual, null, null, false, false, order, $"manual {id}")
        { ManualUrl = url, InstallTier = InstallTier.ManualAfter };

    private static InstallEntry ManualAfter(string id, int order = 500)
        => new(id, "install", "oyun-launcher", InstallMethod.Winget, $"{id}.App", null, false, false, order, $"after {id}")
        { InstallTier = InstallTier.ManualAfter };

    // ----------------------------------------------------------------------------------------------------
    // Step 6: InstallPlanExport.Build is a pure, zero-IO classification of the three planner channels.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Build_maps_a_winget_action_to_a_Reinstall_item_with_the_winget_id()
    {
        InstallPlanResult plan = Plan(Winget("git", "Git.Git", admin: true));

        InstallPlanExportDoc doc = InstallPlanExport.Build(plan, new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        Assert.Equal(InstallItemClass.Reinstall, item.Class);
        Assert.Equal("git", item.EntryId);
        Assert.Equal(InstallMethod.Winget, item.Method);
        Assert.Equal("Git.Git", item.WingetId);
        Assert.Null(item.NpmPackage);
        Assert.True(item.RequiresAdmin);
        Assert.Null(item.Channel);                            // locked decision #2: NOT populated in this slice
        Assert.Equal(InstallPlanExport.SchemaVersion, doc.SchemaVersion);
        Assert.Equal(T0, doc.GeneratedUtc);
    }

    [Fact]
    public void Build_maps_an_npm_action_to_a_Reinstall_item_with_the_package_name()
    {
        InstallPlanResult plan = Plan(Npm("claude", "@anthropic-ai/claude-code"));

        InstallPlanItem item = Assert.Single(InstallPlanExport.Build(plan, new FakeClock(T0)).Items);

        Assert.Equal(InstallItemClass.Reinstall, item.Class);
        Assert.Equal(InstallMethod.Npm, item.Method);
        Assert.Equal("@anthropic-ai/claude-code", item.NpmPackage);
        Assert.Null(item.WingetId);
    }

    [Fact]
    public void Build_maps_a_config_restore_action_to_a_Copy_item()
    {
        InstallPlanResult plan = Plan(Config("vscode", @"C:\src\settings.json", @"C:\dst\settings.json"));

        InstallPlanItem item = Assert.Single(InstallPlanExport.Build(plan, new FakeClock(T0)).Items);

        Assert.Equal(InstallItemClass.Copy, item.Class);
        Assert.Equal(InstallMethod.ConfigRestore, item.Method);
        Assert.Equal("vscode", item.EntryId);
    }

    [Fact]
    public void Build_maps_a_url_manual_entry_to_a_ManualUrl_item()
    {
        InstallPlanResult plan = Plan(UrlManual("chrome", "https://example.test/chrome"));

        InstallPlanItem manual = Assert.Single(
            InstallPlanExport.Build(plan, new FakeClock(T0)).Items,
            i => i.Class == InstallItemClass.ManualUrl);

        Assert.Equal("chrome", manual.EntryId);
        Assert.Equal(InstallMethod.UrlManual, manual.Method);
    }

    [Fact]
    public void Build_maps_a_manual_after_entry_to_a_ManualAfter_item()
    {
        InstallPlanResult plan = Plan(ManualAfter("steam"));

        InstallPlanItem manual = Assert.Single(
            InstallPlanExport.Build(plan, new FakeClock(T0)).Items,
            i => i.Class == InstallItemClass.ManualAfter);

        Assert.Equal("steam", manual.EntryId);
    }

    [Fact]
    public void Build_maps_an_auth_key_entry_to_a_Login_item_with_ONLY_the_short_key_and_no_path()
    {
        // An auth-key entry carrying a probe path + sign-in command + a secret-looking source path. Locked
        // decision #3: only the short key ("claude") + Class=Login may travel off-machine.
        var entry = new InstallEntry(
            "claude-login", "install", "ai-cli", InstallMethod.UrlManual,
            null, null, false, false, 600, "Sign in to Claude")
        {
            InstallTier = InstallTier.ManualAfter,
            AuthKey = "claude",
            AuthProbe = @"C:\Users\alice\.claude\.credentials.json",
            AuthCommand = "claude login --secret SUPERSECRET",
            ManualUrl = "https://claude.ai/login?token=SUPERSECRET",
        };
        InstallPlanResult plan = Plan(entry);

        InstallPlanItem login = Assert.Single(
            InstallPlanExport.Build(plan, new FakeClock(T0)).Items,
            i => i.Class == InstallItemClass.Login);

        // The label is the short key ONLY — never the path/command/secret.
        Assert.Equal("claude", login.Description);
        Assert.DoesNotContain(@"C:\Users", login.Description);
        Assert.DoesNotContain(".credentials", login.Description);
        Assert.DoesNotContain("SUPERSECRET", login.Description);
        Assert.DoesNotContain("login --secret", login.Description);
    }

    [Fact]
    public void Build_maps_a_gate_blocked_skip_to_an_Excluded_item_with_the_reason()
    {
        // Build the result directly: a gate-blocked entry that produced no action and is reported as a skip.
        var entry = Winget("evil", "Some.Blocked");
        var emptyPlan = new OperationPlan("Reinstall apps and restore settings", "install",
            System.Array.Empty<PlannedAction>(), T0);
        var result = new InstallPlanResult(
            emptyPlan,
            new[] { new InstallSkip(entry, InstallSkipReason.GateBlocked, "command denied by policy") },
            System.Array.Empty<InstallEntry>());

        InstallPlanItem excluded = Assert.Single(InstallPlanExport.Build(result, new FakeClock(T0)).Items);

        Assert.Equal(InstallItemClass.Excluded, excluded.Class);
        Assert.Equal("evil", excluded.EntryId);
        Assert.Equal(nameof(InstallSkipReason.GateBlocked), excluded.SkipReason);
    }

    [Fact]
    public void Build_does_not_duplicate_a_manual_entry_that_is_also_reported_as_a_skip()
    {
        // The planner reports a url-manual entry in BOTH the ManualChecklist and the Skipped list. The export
        // must surface it ONCE (as the manual item), never twice.
        InstallPlanResult plan = Plan(UrlManual("chrome", "https://example.test/chrome"));

        InstallPlanExportDoc doc = InstallPlanExport.Build(plan, new FakeClock(T0));

        InstallPlanItem only = Assert.Single(doc.Items, i => i.EntryId == "chrome");
        Assert.Equal(InstallItemClass.ManualUrl, only.Class);
    }

    [Fact]
    public void Build_orders_executable_items_by_the_real_restore_order_not_entry_id_alphabetic()
    {
        // F3: the executable winget/npm/config actions do not carry RestoreOrder on the action record. The export
        // must read the planner's authoritative entry → RestoreOrder map, NOT fall back to an entry-id alphabetic
        // sort. The ids here are deliberately ANTI-alphabetic vs their order: "zzz" runs FIRST (order 1), "aaa"
        // LAST (order 3). An alphabetic sort would emit aaa, mmm, zzz — the wrong sequence.
        InstallPlanResult plan = Plan(
            Winget("zzz", "Zzz.App", order: 1),
            Npm("mmm", "left-pad", order: 2),
            Config("aaa", @"C:\Users\victim\src\settings.json", @"C:\Users\victim\dst\settings.json", order: 3));

        InstallPlanExportDoc doc = InstallPlanExport.Build(plan, new FakeClock(T0));

        Assert.Equal(3, doc.Items.Count);
        // Real restore order (zzz → mmm → aaa), not the id-alphabetic (aaa → mmm → zzz) it would degrade to.
        Assert.Equal(new[] { "zzz", "mmm", "aaa" }, doc.Items.Select(i => i.EntryId).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, doc.Items.Select(i => i.RestoreOrder).ToArray());
    }

    // ----------------------------------------------------------------------------------------------------
    // Step 7: WriteExport produces a golden install_plan.json under a temp root, gate-checked.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void WriteExport_writes_a_deterministic_json_file_into_the_payload_dir()
    {
        using var ws = new TempWorkspace("wck-install-export-");

        var doc = new InstallPlanExportDoc(InstallPlanExport.SchemaVersion, T0, new[]
        {
            new InstallPlanItem("git", InstallItemClass.Reinstall, InstallMethod.Winget, "Git.Git", null, false, 100, null, "Install git"),
            new InstallPlanItem("claude", InstallItemClass.Login, InstallMethod.UrlManual, null, null, false, 600, null, "claude"),
        });

        string path = new InstallPlanWriter().WriteExport(doc, ws.Root, RealGate());

        Assert.True(File.Exists(path));
        Assert.EndsWith(InstallPlanFiles.Plan, path);

        string json = File.ReadAllText(path);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("2026-01-01T12:30:00", json);
        Assert.Contains("\"entryId\": \"git\"", json);
        Assert.Contains("\"wingetId\": \"Git.Git\"", json);
        // The enum serializes as a camelCase string, not an int.
        Assert.Contains("\"class\": \"reinstall\"", json);
        Assert.Contains("\"class\": \"login\"", json);
        // locked decision #2: the channel is null and is omitted from the JSON entirely (never serialized).
        Assert.DoesNotContain("channel", json);
    }

    // ----------------------------------------------------------------------------------------------------
    // Step 7b: redaction — no auth probe path / sign-in command / secret is ever serialized (locked #3).
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void WriteExport_never_serializes_an_auth_probe_path_command_or_secret()
    {
        using var ws = new TempWorkspace("wck-install-redact-");

        var entry = new InstallEntry(
            "claude-login", "install", "ai-cli", InstallMethod.UrlManual,
            null, null, false, false, 600, "Sign in to Claude")
        {
            InstallTier = InstallTier.ManualAfter,
            AuthKey = "claude",
            AuthProbe = @"C:\Users\alice\.claude\.credentials.json",
            AuthCommand = "claude login --secret SUPERSECRET",
            ManualUrl = "https://claude.ai/login?token=SUPERSECRET",
        };
        InstallPlanResult plan = Plan(entry);
        InstallPlanExportDoc doc = InstallPlanExport.Build(plan, new FakeClock(T0));

        string path = new InstallPlanWriter().WriteExport(doc, ws.Root, RealGate());
        string json = File.ReadAllText(path);

        // The structural-redaction discipline: the short key is present, the sensitive material is NOT.
        Assert.Contains("claude", json);                       // the key label is allowed
        Assert.DoesNotContain("SUPERSECRET", json);            // no secret/token
        Assert.DoesNotContain(".credentials", json);           // no probe path
        Assert.DoesNotContain("authProbe", json);              // no probe field at all
        Assert.DoesNotContain("authCommand", json);            // no sign-in command field
        Assert.DoesNotContain(@"C:\\Users", json);             // no source/profile path
    }

    [Fact]
    public void WriteExport_leaks_no_path_secret_or_url_token_for_ANY_item_class()
    {
        // F1: a FULL plan exercising every item class, each carrying a leak marker — a victim profile path on the
        // config source AND destination, a winget id, an npm package, a manual URL with a token, and an auth-key
        // entry whose probe path / sign-in command / login token all look like secrets. The ENTIRE produced JSON
        // must contain NONE of the file-system paths, profile names, config destinations, URL tokens, or secrets.
        using var ws = new TempWorkspace("wck-install-redact-all-");

        const string VictimSrc = @"C:\Users\victim\AppData\Roaming\App\config.json";
        const string VictimDst = @"C:\Users\victim\AppData\Roaming\App\restored.json";

        var login = new InstallEntry(
            "claude-login", "install", "ai-cli", InstallMethod.UrlManual,
            null, null, false, false, 600, @"Sign in; creds at C:\Users\victim\.claude\.credentials.json")
        {
            InstallTier = InstallTier.ManualAfter,
            AuthKey = "claude",
            AuthProbe = @"C:\Users\victim\AppData\Roaming\App\.credentials.json",
            AuthCommand = "claude login --secret SUPERSECRET",
            ManualUrl = "https://claude.ai/login?token=SUPERSECRETTOKEN",
        };

        InstallPlanResult plan = Plan(
            Winget("git", "Git.Git", order: 1),
            Npm("claude-cli", "@anthropic-ai/claude-code", order: 2),
            Config("vscode", VictimSrc, VictimDst, order: 3),
            UrlManual("chrome", "https://dl.example.test/chrome?key=DOWNLOADSECRET", order: 4),
            login);

        InstallPlanExportDoc doc = InstallPlanExport.Build(plan, new FakeClock(T0));
        string path = new InstallPlanWriter().WriteExport(doc, ws.Root, RealGate());
        string json = File.ReadAllText(path);

        // No file-system paths / profile identifiers anywhere in the document (escaped and raw forms).
        Assert.DoesNotContain("C:\\Users", json);              // escaped backslash form as serialized
        Assert.DoesNotContain(@"C:\Users", json);              // raw form (defensive)
        Assert.DoesNotContain("victim", json);                 // the profile name
        Assert.DoesNotContain("AppData", json);                // any profile sub-path
        Assert.DoesNotContain("%USERPROFILE%", json);          // env-expanded profile form
        Assert.DoesNotContain("config.json", json);            // the raw config source name
        Assert.DoesNotContain("restored.json", json);          // the raw config destination name
        Assert.DoesNotContain(".credentials", json);           // the probe path leaf
        Assert.DoesNotContain("Roaming", json);                // any roaming-profile segment

        // No secrets / URL tokens from any class (Login auth command/url, manual download url).
        Assert.DoesNotContain("SUPERSECRET", json);            // login secret + token
        Assert.DoesNotContain("DOWNLOADSECRET", json);         // manual-url token
        Assert.DoesNotContain("login --secret", json);         // the sign-in command
        Assert.DoesNotContain("claude.ai/login", json);        // the login url
        Assert.DoesNotContain("dl.example.test", json);        // the manual download host

        // The auth/redaction field NAMES must never appear as serialized properties. (Note: "manualUrl" is NOT
        // asserted here — it is the legitimate camelCase value of the ManualUrl enum class, not a leaked field; the
        // URL itself is already proven absent by the host/token assertions above.)
        Assert.DoesNotContain("authProbe", json);
        Assert.DoesNotContain("authCommand", json);
        Assert.DoesNotContain("\"manualUrl\":", json);         // no manual-url FIELD (the enum value is allowed)

        // Sanity: the safe, allow-listed labels DID travel (proves the items are present, not silently dropped).
        Assert.Contains("Git.Git", json);                      // allow-listed winget id (path-free)
        Assert.Contains("@anthropic-ai/claude-code", json);    // allow-listed npm package (path-free)
        Assert.Contains("claude", json);                       // the short auth key for the Login item
    }

    [Fact]
    public void WriteExport_drops_a_path_shaped_wingetId_on_a_manual_after_entry()
    {
        // F2 (auditor MAJOR): a manual-after entry whose wingetId is a path, not a package id. The loader only trims
        // the manifest value — it never shape-checks — so without the export's allow-list pass this raw path would
        // be copied straight into the ManualAfter item and serialized. The export MUST run it through the SAME
        // allow-list that gates the executable --id and drop a non-package value to null.
        using var ws = new TempWorkspace("wck-install-manual-wingetid-");

        var entry = new InstallEntry(
            "evil-after", "install", "oyun-launcher", InstallMethod.Winget,
            @"C:\Users\victim\secret", null, false, false, 700, "after evil")
        {
            InstallTier = InstallTier.ManualAfter,
        };
        InstallPlanResult plan = Plan(entry);

        InstallPlanExportDoc doc = InstallPlanExport.Build(plan, new FakeClock(T0));

        InstallPlanItem after = Assert.Single(doc.Items, i => i.Class == InstallItemClass.ManualAfter);
        // The path-shaped id is dropped, not copied.
        Assert.Null(after.WingetId);

        // And it never reaches the serialized JSON (the whole document).
        string path = new InstallPlanWriter().WriteExport(doc, ws.Root, RealGate());
        string json = File.ReadAllText(path);
        Assert.DoesNotContain("C:\\Users", json);
        Assert.DoesNotContain(@"C:\Users", json);
        Assert.DoesNotContain("victim", json);
        Assert.DoesNotContain("secret", json);
    }

    [Fact]
    public void WriteExport_drops_a_path_shaped_npmPackage_and_wingetId_on_an_excluded_skip()
    {
        // F2 (auditor MAJOR): a skip (Excluded) whose npmPackage/wingetId are path/token-shaped raw manifest values.
        // The Excluded branch used to copy skip.Entry.WingetId / skip.Entry.NpmPackage verbatim. The export MUST run
        // both through the allow-list and emit null for a non-package value, so nothing path-shaped reaches the JSON.
        using var ws = new TempWorkspace("wck-install-excluded-pkg-");

        var entry = new InstallEntry(
            "evil-skip", "install", "ai-cli", InstallMethod.Npm,
            @"C:\Users\victim\winget-secret", @"C:\Users\victim\npm-secret", false, false, 800, "skip evil");
        var emptyPlan = new OperationPlan("Reinstall apps and restore settings", "install",
            System.Array.Empty<PlannedAction>(), T0);
        var result = new InstallPlanResult(
            emptyPlan,
            new[] { new InstallSkip(entry, InstallSkipReason.Incomplete, "missing data") },
            System.Array.Empty<InstallEntry>());

        InstallPlanExportDoc doc = InstallPlanExport.Build(result, new FakeClock(T0));

        InstallPlanItem excluded = Assert.Single(doc.Items, i => i.Class == InstallItemClass.Excluded);
        // Both path-shaped package fields are dropped, not copied.
        Assert.Null(excluded.WingetId);
        Assert.Null(excluded.NpmPackage);

        // And neither reaches the serialized JSON (the whole document).
        string path = new InstallPlanWriter().WriteExport(doc, ws.Root, RealGate());
        string json = File.ReadAllText(path);
        Assert.DoesNotContain("C:\\Users", json);
        Assert.DoesNotContain(@"C:\Users", json);
        Assert.DoesNotContain("victim", json);
        Assert.DoesNotContain("secret", json);
    }

    [Fact]
    public void WriteExport_leaks_no_raw_value_from_a_fully_hostile_manifest_on_ANY_field()
    {
        // F4 (systemic whitelist guarantee): a SINGLE hostile manifest where EVERY raw manifest string field carries
        // its own distinct leak marker — ConfigSource/Destination, the method token, wingetId, npmPackage, authProbe,
        // authCommand, manualUrl, the skip note (built from manualUrl), and the user-authored description. EntryId is
        // left safe by contract. After Build → full JSON serialize, NONE of the markers may appear, and every
        // affected item field must be null or a derived constant. (Pre-fix the method markers FAILED.)
        using var ws = new TempWorkspace("wck-install-hostile-all-");

        const string CfgSrc = @"C:\Users\victim\AppData\Roaming\srcSECRET.json";
        const string CfgDst = @"C:\Users\victim\AppData\Roaming\dstSECRET.json";
        const string EvilMethod = @"C:\Users\victim\evilMethodSECRET.exe";
        const string WingetPath = @"C:\Users\victim\wingetSECRET";
        const string NpmPath = @"C:\Users\victim\npmSECRET";
        const string ProbePath = @"C:\Users\victim\AppData\Roaming\.credentials.json";
        const string AuthCmd = "claude login --secret AUTHCMDSECRET";
        const string LoginUrl = "https://evil.test/login?token=LOGINURLSECRET";
        const string ManualDlUrl = "https://dl.evil.test/app?key=MANUALURLSECRET";
        const string EvilDesc = @"Sign in; creds at C:\Users\victim\.claude\DESCSECRET.json";

        // 1) config-restore: ConfigSource + ConfigDestination must never travel.
        var config = new InstallEntry(
            "cfg", "install", "config", InstallMethod.ConfigRestore,
            null, null, false, false, 10, EvilDesc)
        { ConfigSource = CfgSrc, ConfigDestination = CfgDst };

        // 2) a manual-after entry whose METHOD itself is a path (default tier auto + non-url-manual + non-automatable
        //    → manual checklist + ManualAfter). The raw method must be dropped to "" (path-shaped-method negative).
        var pathMethod = new InstallEntry(
            "evil-method", "install", "oyun-launcher", EvilMethod,
            WingetPath, NpmPath, false, false, 20, EvilDesc);

        // 3) a url-manual entry whose ManualUrl carries a token (the skip note embeds the url).
        var urlManual = new InstallEntry(
            "evil-url", "install", "tarayici", InstallMethod.UrlManual,
            null, null, false, false, 30, EvilDesc)
        { ManualUrl = ManualDlUrl, InstallTier = InstallTier.ManualAfter };

        // 4) a login entry: probe path + sign-in command + login-url token + a path-bearing description.
        var login = new InstallEntry(
            "evil-login", "install", "ai-cli", InstallMethod.UrlManual,
            null, null, false, false, 40, EvilDesc)
        {
            InstallTier = InstallTier.ManualAfter,
            AuthKey = "claude",
            AuthProbe = ProbePath,
            AuthCommand = AuthCmd,
            ManualUrl = LoginUrl,
        };

        InstallPlanResult plan = Plan(config, pathMethod, urlManual, login);

        // 5) an Excluded skip whose method/wingetId/npmPackage/description are all raw path-shaped manifest values.
        var excludedEntry = new InstallEntry(
            "evil-skip", "install", "ai-cli", EvilMethod,
            WingetPath, NpmPath, false, false, 50, EvilDesc);
        var skipped = plan.Skipped.ToList();
        skipped.Add(new InstallSkip(excludedEntry, InstallSkipReason.Incomplete, "missing data"));
        plan = plan with { Skipped = skipped };

        InstallPlanExportDoc doc = InstallPlanExport.Build(plan, new FakeClock(T0));

        // ---- field-level assertions: every affected item field is null or a derived constant -----------------
        InstallPlanItem cfgItem = Assert.Single(doc.Items, i => i.EntryId == "cfg");
        Assert.Equal(InstallMethod.ConfigRestore, cfgItem.Method);     // derived constant, not the raw method
        Assert.Null(cfgItem.WingetId);
        Assert.Null(cfgItem.NpmPackage);

        InstallPlanItem methodItem = Assert.Single(doc.Items, i => i.EntryId == "evil-method");
        Assert.Equal(InstallItemClass.ManualAfter, methodItem.Class);
        Assert.Equal(string.Empty, methodItem.Method);                 // path-shaped method dropped to ""
        Assert.Null(methodItem.WingetId);                              // path-shaped winget id dropped
        Assert.Null(methodItem.NpmPackage);                            // path-shaped npm package dropped

        InstallPlanItem loginItem = Assert.Single(doc.Items, i => i.EntryId == "evil-login");
        Assert.Equal(InstallMethod.UrlManual, loginItem.Method);       // a known token survives
        Assert.Equal("claude", loginItem.Description);                 // ONLY the short key

        InstallPlanItem skipItem = Assert.Single(doc.Items, i => i.EntryId == "evil-skip");
        Assert.Equal(InstallItemClass.Excluded, skipItem.Class);
        Assert.Equal(string.Empty, skipItem.Method);                   // path-shaped method dropped to ""
        Assert.Null(skipItem.WingetId);
        Assert.Null(skipItem.NpmPackage);

        // ---- whole-document serialize: NONE of the markers may appear -----------------------------------------
        string path = new InstallPlanWriter().WriteExport(doc, ws.Root, RealGate());
        string json = File.ReadAllText(path);

        foreach (string marker in new[]
        {
            // every distinct per-field secret marker
            "srcSECRET", "dstSECRET", "evilMethodSECRET", "wingetSECRET", "npmSECRET",
            "DESCSECRET", "AUTHCMDSECRET", "LOGINURLSECRET", "MANUALURLSECRET",
            // generic path / profile / secret markers
            "C:\\Users", @"C:\Users", "victim", "AppData", "Roaming", "secret",
            ".credentials", "login --secret", "evil.test",
            // raw full values (defensive)
            CfgSrc, CfgDst, EvilMethod, WingetPath, NpmPath, ProbePath, AuthCmd, LoginUrl, ManualDlUrl, EvilDesc,
        })
        {
            Assert.DoesNotContain(marker, json);
        }

        // the auth/redaction field NAMES never appear as serialized properties.
        Assert.DoesNotContain("authProbe", json);
        Assert.DoesNotContain("authCommand", json);
        Assert.DoesNotContain("\"manualUrl\":", json);

        // Sanity: the one safe value (the short auth key) DID travel — proves items are present, not silently empty.
        Assert.Contains("claude", json);
    }

    [Fact]
    public void WriteExport_refuses_a_gate_blocked_destination()
    {
        var doc = new InstallPlanExportDoc(InstallPlanExport.SchemaVersion, T0, System.Array.Empty<InstallPlanItem>());
        Assert.Throws<UnauthorizedAccessException>(() =>
            new InstallPlanWriter().WriteExport(doc, @"C:\Windows\wck-evil", RealGate()));
    }

    // ----------------------------------------------------------------------------------------------------
    // Step 8: InstallRunner.ExportPlan writes into the temp root; a refusal writes nothing.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ExportPlan_writes_install_plan_json_into_the_payload_root()
    {
        using var ws = new TempWorkspace("wck-install-runner-");
        InstallPlanResult plan = Plan(Winget("git", "Git.Git"), Npm("claude", "@anthropic-ai/claude-code"));

        var runner = new InstallRunner(new InstallPlanWriter(), new FakeClock(T0));
        InstallRunResult result = runner.ExportPlan(plan, ws.Root, RealGate());

        Assert.True(result.Authorized);
        Assert.Equal(2, result.Export.Items.Count);
        Assert.True(File.Exists(Path.Combine(ws.Root, InstallPlanFiles.Plan)));
    }

    [Fact]
    public void ExportPlan_refusal_writes_nothing()
    {
        InstallPlanResult plan = Plan(Winget("git", "Git.Git"));

        var runner = new InstallRunner(new InstallPlanWriter(), new FakeClock(T0));
        // A protected/system target is refused by the gate; the runner reports unauthorized and writes nothing.
        string evilRoot = @"C:\Windows\wck-evil";
        InstallRunResult result = runner.ExportPlan(plan, evilRoot, RealGate());

        Assert.False(result.Authorized);
        Assert.False(File.Exists(Path.Combine(evilRoot, InstallPlanFiles.Plan)));
    }

    // ----------------------------------------------------------------------------------------------------
    // Step 9: invariant — the export step NEVER produces a new gated action; it reads the plan + writes JSON.
    // The ONLY gate evaluation is the single synthetic write-target probe for the payload root.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Export_step_never_produces_a_new_gated_action()
    {
        using var ws = new TempWorkspace("wck-install-invariant-");
        // A plan with two real actions: the export must NOT re-gate them — it only gates the write target ONCE.
        InstallPlanResult plan = Plan(Winget("git", "Git.Git"), Config("vscode", @"C:\s", @"C:\d"));

        var counting = new CountingGate(RealGate());
        var runner = new InstallRunner(new InstallPlanWriter(), new FakeClock(T0));
        runner.ExportPlan(plan, ws.Root, counting);

        // EXACTLY ONE gate evaluation — the synthetic CopyAction write-target probe for the payload root — and
        // that probe is never added to an executed plan. The two plan actions are not re-evaluated by the export.
        Assert.Equal(1, counting.EvaluateCount);
        Assert.All(counting.EvaluatedKinds, kind => Assert.Equal("copy", kind));
        Assert.Single(counting.EvaluatedKinds);
    }

    // ---- test doubles ------------------------------------------------------------------------------------

    /// <summary>Wraps a real gate and tallies every <see cref="ISafetyGate.Evaluate"/> call + the action kind judged.</summary>
    private sealed class CountingGate : ISafetyGate
    {
        private readonly ISafetyGate _inner;
        public CountingGate(ISafetyGate inner) => _inner = inner;

        public int EvaluateCount { get; private set; }
        public List<string> EvaluatedKinds { get; } = new();

        public SafetyVerdict Evaluate(PlannedAction action)
        {
            EvaluateCount++;
            EvaluatedKinds.Add(action.Kind);
            return _inner.Evaluate(action);
        }

        public PlanValidationResult Validate(OperationPlan plan) => _inner.Validate(plan);
    }
}

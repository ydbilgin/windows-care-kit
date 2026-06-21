using System.IO;
using System.Linq;
using System.Text.Json;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// RED-TEAM adversarial probes of the install-export redaction discipline (Step 3 EXPORT slice). These tests
/// do NOT anchor to the production-author's expectations: each one feeds a HOSTILE manifest (the loader only
/// trims raw fields — it never shape-checks) through <see cref="InstallPlanExport.Build"/> and then serializes
/// the document the exact way <see cref="InstallPlanWriter"/> does, asserting that NO file-system path, secret,
/// URL token, or escape-breakout reaches the JSON — EXCEPT where the production code explicitly contracts a
/// verbatim channel (EntryId, and the Login auth key). Those by-contract channels are pinned by separate tests
/// that DOCUMENT the residual leak surface (so a future Import gate has a regression anchor).
///
/// HOST-SAFETY: pure in-memory plan results + a single TempWorkspace under %TEMP% for the serialize step.
/// Nothing is installed, no process spawns, no real profile/registry is touched.
/// </summary>
public class InstallPlanExportRedTeamTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc);

    private static ISafetyGate RealGate()
        => new SafetyGate(ProtectedResources.ForCurrentSystem(), new FakeCanonicalizer());

    /// <summary>Serialize the export document with the SAME options the production writer uses (camelCase + enum strings).</summary>
    private static readonly JsonSerializerOptions WriterJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static string Serialize(InstallPlanExportDoc doc) => JsonSerializer.Serialize(doc, WriterJson);

    /// <summary>A plan result with only a skip list — the cheapest way to push a raw hostile entry through the Excluded branch.</summary>
    private static InstallPlanResult SkipOnly(InstallEntry entry, InstallSkipReason reason = InstallSkipReason.Incomplete)
    {
        var emptyPlan = new OperationPlan("Reinstall apps and restore settings", "install",
            Array.Empty<PlannedAction>(), T0);
        return new InstallPlanResult(
            emptyPlan,
            new[] { new InstallSkip(entry, reason, "note") },
            Array.Empty<InstallEntry>());
    }

    /// <summary>A plan result with only a manual-checklist entry — pushes a raw hostile entry through FromManual.</summary>
    private static InstallPlanResult ManualOnly(InstallEntry entry)
    {
        var emptyPlan = new OperationPlan("Reinstall apps and restore settings", "install",
            Array.Empty<PlannedAction>(), T0);
        return new InstallPlanResult(
            emptyPlan,
            Array.Empty<InstallSkip>(),
            new[] { entry });
    }

    private static InstallEntry RawEntry(
        string id,
        string method,
        string? wingetId = null,
        string? npmPackage = null,
        int order = 100,
        string description = "desc")
        => new(id, "install", "cat", method, wingetId, npmPackage, false, false, order, description);

    // ====================================================================================================
    // 1) EMBEDDED NEWLINE in a winget id / npm package on the skip channel.
    //    The allow-list regex is anchored ^...$ but in .NET (no Singleline) '$' also matches just BEFORE a
    //    trailing '\n'. A trailing-newline id could pass IsMatch; .Trim() should then strip it. Verify nothing
    //    with a newline (or a smuggled second line) reaches the JSON.
    // ====================================================================================================

    [Fact]
    public void Excluded_wingetId_with_a_trailing_newline_does_not_emit_a_multiline_value()
    {
        // .NET regex (no Singleline) lets '$' match BEFORE a single trailing '\n', so "Git.Git\n" passes the
        // anchored allow-list. The redaction safety net is the subsequent .Trim(). Pin BOTH facts: the value is
        // accepted (path is live, not a false-green) AND the emitted token carries no newline.
        Assert.True(InstallPlanner.IsValidWingetId("Git.Git\n"),
            "expected the anchored regex to accept a single trailing newline (documents the .NET '$' semantics)");

        var entry = RawEntry("nl-winget", InstallMethod.Winget, wingetId: "Git.Git\n");
        InstallPlanExportDoc doc = InstallPlanExport.Build(SkipOnly(entry), new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        // .Trim() neutralizes the trailing newline → the emitted token is the clean id, never a multiline value.
        Assert.Equal("Git.Git", item.WingetId);
        Assert.DoesNotContain("\n", item.WingetId);
        Assert.DoesNotContain("\r", item.WingetId);

        string json = Serialize(doc);
        // The serialized winget id value must not contain a literal newline inside the string token.
        Assert.DoesNotContain("Git.Git\\n", json);
    }

    [Fact]
    public void Excluded_wingetId_with_an_INTERIOR_newline_is_dropped_to_null()
    {
        // An interior newline ("Git.Git\nrm -rf") cannot satisfy ^[A-Za-z0-9.+_-]+$ → must be rejected, never emitted.
        var entry = RawEntry("interior-nl", InstallMethod.Winget, wingetId: "Git.Git\nSECONDLINE");
        InstallPlanExportDoc doc = InstallPlanExport.Build(SkipOnly(entry), new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        Assert.Null(item.WingetId);
        Assert.DoesNotContain("SECONDLINE", Serialize(doc));
    }

    [Fact]
    public void Excluded_npmPackage_with_an_interior_newline_is_dropped_to_null()
    {
        var entry = RawEntry("nl-npm", InstallMethod.Npm, npmPackage: "left-pad\nSECONDLINE");
        InstallPlanExportDoc doc = InstallPlanExport.Build(SkipOnly(entry), new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        Assert.Null(item.NpmPackage);
        Assert.DoesNotContain("SECONDLINE", Serialize(doc));
    }

    // ====================================================================================================
    // 2) UNICODE-encoded path / homoglyph smuggled into a winget id. Backslash, slash, colon are NOT in the
    //    allow-list, but a fullwidth-solidus / reverse-solidus look-alike could slip a "path-looking" value
    //    that is technically allow-list-valid. Document what survives (must contain no ASCII path separators).
    // ====================================================================================================

    [Fact]
    public void Excluded_wingetId_with_a_unicode_lookalike_separator_emits_no_ascii_path_separator()
    {
        // U+2215 DIVISION SLASH and U+FF0F FULLWIDTH SOLIDUS are NOT in [A-Za-z0-9.+_-] → must be rejected.
        var entry = RawEntry("uni-sep", InstallMethod.Winget, wingetId: "C∕Users∕victim");
        InstallPlanExportDoc doc = InstallPlanExport.Build(SkipOnly(entry), new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        Assert.Null(item.WingetId);                 // a non-ASCII separator is not in the class → dropped
        string json = Serialize(doc);
        Assert.DoesNotContain("victim", json);
    }

    // ====================================================================================================
    // 3) VERY LONG winget id — the allow-list regex has no length cap. A 64 KB "valid-shape" id passes and is
    //    emitted verbatim. This is allow-list-valid (no path/secret chars), so it is NOT a redaction leak, but
    //    we pin that the export does not crash and the value, if present, is still separator-free.
    // ====================================================================================================

    [Fact]
    public void Excluded_extremely_long_but_shape_valid_wingetId_does_not_crash_and_stays_separator_free()
    {
        string huge = "A" + new string('a', 64 * 1024);   // 64 KiB, all in-class
        var entry = RawEntry("huge", InstallMethod.Winget, wingetId: huge);
        InstallPlanExportDoc doc = InstallPlanExport.Build(SkipOnly(entry), new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        if (item.WingetId is not null)
        {
            Assert.DoesNotContain("\\", item.WingetId);
            Assert.DoesNotContain("/", item.WingetId);
            Assert.DoesNotContain(":", item.WingetId);
        }
        // Serialization must succeed (no throw) for a large value.
        Assert.NotNull(Serialize(doc));
    }

    // ====================================================================================================
    // 4) JSON-ESCAPE BREAKOUT via EntryId. EntryId travels verbatim (by-contract), so it lands in BOTH the
    //    "entryId" field AND the description. A crafted id that LOOKS like it closes the string and injects a
    //    new property must be safely escaped by System.Text.Json — round-tripping back to the SAME single id,
    //    never a real extra "wingetId" property in the object model.
    // ====================================================================================================

    [Fact]
    public void EntryId_that_looks_like_a_json_breakout_is_safely_escaped_and_round_trips()
    {
        const string evilId = "x\",\"wingetId\":\"INJECTED\",\"z\":\"";
        var entry = RawEntry(evilId, InstallMethod.UrlManual);
        InstallPlanExportDoc doc = InstallPlanExport.Build(ManualOnly(entry), new FakeClock(T0));

        string json = Serialize(doc);

        // Round-trip: the parsed document must still have EXACTLY ONE item whose entryId is the literal evil id,
        // and whose wingetId is null — the injection did NOT create a real wingetId property.
        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement items = parsed.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        JsonElement only = items[0];
        Assert.Equal(evilId, only.GetProperty("entryId").GetString());
        // The injected "INJECTED" token is only ever the inner content of the entryId/description strings,
        // never a structural wingetId value.
        Assert.False(only.TryGetProperty("wingetId", out JsonElement wid) && wid.ValueKind == JsonValueKind.String
                     && wid.GetString() == "INJECTED");
    }

    // ====================================================================================================
    // 5) BY-CONTRACT EntryId leak surface (UNTRUSTED-PASSTHROUGH INVARIANT). EntryId is carried VERBATIM and is
    //    NOT redacted/shape-checked. This test DOCUMENTS the present-tense leak: a hostile manifest that puts a
    //    real profile path in the entry id DOES surface that path in entryId + description in install_plan.json
    //    ON DISK. (Not a code bug — a deliberate contract boundary; pinned so any future Import gate has a
    //    regression anchor and the risk is not silently forgotten.)
    // ====================================================================================================

    [Fact]
    public void EntryId_is_carried_verbatim_even_when_it_is_a_profile_path_documenting_the_contract_boundary()
    {
        const string pathId = @"C:\Users\victim\.ssh\id_rsa";
        var entry = RawEntry(pathId, InstallMethod.UrlManual);
        InstallPlanExportDoc doc = InstallPlanExport.Build(ManualOnly(entry), new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        // Current contract: the id (and the SafeLabel-derived description) carry it verbatim. If a future
        // Import-gate sanitizer changes this, THIS test must be revisited (that is the point of the anchor).
        Assert.Equal(pathId, item.EntryId);
        Assert.Contains(pathId, item.Description);
        Assert.Contains("victim", Serialize(doc));   // documents that the path DOES reach the JSON via EntryId
    }

    // ====================================================================================================
    // 6) BY-CONTRACT AuthKey leak surface. Locked decision #3 says ONLY the "short auth key" travels for a
    //    Login item — but nothing enforces shortness/shape: AuthKey is emitted verbatim (Trim only). A hostile
    //    manifest can stuff a secret into AuthKey and it WILL be serialized. Pin this so the contract is
    //    explicit and the future Import gate has an anchor.
    // ====================================================================================================

    [Fact]
    public void AuthKey_is_emitted_verbatim_even_when_it_carries_a_secret_documenting_the_contract_boundary()
    {
        var entry = new InstallEntry(
            "login", "install", "ai-cli", InstallMethod.UrlManual,
            null, null, false, false, 100, "Sign in")
        {
            InstallTier = InstallTier.ManualAfter,
            AuthKey = "claude TOKEN=sk-LEAKEDSECRET",   // a "short key" the loader did not shape-check
            AuthProbe = @"C:\Users\victim\.creds",
            AuthCommand = "login --secret SUPERSECRET",
            ManualUrl = "https://x.test/login?token=URLSECRET",
        };
        InstallPlanExportDoc doc = InstallPlanExport.Build(ManualOnly(entry), new FakeClock(T0));

        InstallPlanItem login = Assert.Single(doc.Items, i => i.Class == InstallItemClass.Login);
        // Contract boundary: the WHOLE AuthKey value travels (only trimmed).
        Assert.Equal("claude TOKEN=sk-LEAKEDSECRET", login.Description);

        string json = Serialize(doc);
        // The probe path, the sign-in command, and the URL token are STILL fully redacted (those are NOT carried).
        Assert.DoesNotContain(".creds", json);
        Assert.DoesNotContain("SUPERSECRET", json);
        Assert.DoesNotContain("URLSECRET", json);
        Assert.DoesNotContain("authProbe", json);
        Assert.DoesNotContain("authCommand", json);
        // But the AuthKey payload (the by-contract verbatim channel) DOES travel.
        Assert.Contains("sk-LEAKEDSECRET", json);
    }

    // ====================================================================================================
    // 6b) UNTRUSTED-PASSTHROUGH INVARIANT (consolidating regression anchor). EntryId, the Login auth key, and the
    //     Login Description are UNTRUSTED verbatim passthrough: they are written into install_plan.json ON DISK with
    //     NO redaction or shape-check. This is a PRESENT-TENSE data-at-rest contract boundary (the exported file may
    //     be synced/shared/carried in a migration bundle), not merely a future-importer concern. Any consumer MUST
    //     shape-validate these before using them as a path/command/lookup-key/credential. If a future change starts
    //     sanitizing them, THIS test is the deliberate tripwire to revisit the contract.
    // ====================================================================================================

    [Fact]
    public void EntryId_and_Login_AuthKey_and_Description_are_UNTRUSTED_verbatim_passthrough_a_future_importer_MUST_validate()
    {
        const string pathId = @"C:\Users\victim\.aws\credentials";
        const string secretKey = "claude TOKEN=sk-INVARIANT-LEAK";

        // A non-Login reinstall entry whose id is a filesystem path, and a Login entry whose auth key is a secret
        // (with the genuinely-redacted channels also populated, to prove THIS exception is narrow, not a blanket hole).
        InstallEntry reinstall = RawEntry(pathId, InstallMethod.UrlManual);
        var login = new InstallEntry(
            "login", "install", "ai-cli", InstallMethod.UrlManual,
            null, null, false, false, 100, "Sign in")
        {
            InstallTier = InstallTier.ManualAfter,
            AuthKey = secretKey,
            AuthProbe = @"C:\Users\victim\.creds",
            AuthCommand = "login --secret SUPERSECRET",
            ManualUrl = "https://x.test/login?token=URLSECRET",
        };
        var result = new InstallPlanResult(
            new OperationPlan("Reinstall apps and restore settings", "install", Array.Empty<PlannedAction>(), T0),
            Array.Empty<InstallSkip>(),
            new[] { reinstall, login });

        InstallPlanExportDoc doc = InstallPlanExport.Build(result, new FakeClock(T0));
        string json = Serialize(doc);

        // INVARIANT (present-tense): the path-shaped EntryId reaches the on-disk JSON verbatim (entryId + description).
        InstallPlanItem pathItem = Assert.Single(doc.Items, i => i.EntryId == pathId);
        Assert.Contains(pathId, pathItem.Description);
        Assert.Contains("victim", json);

        // INVARIANT (present-tense): the Login auth key (a secret) reaches the on-disk JSON verbatim (description).
        InstallPlanItem loginItem = Assert.Single(doc.Items, i => i.Class == InstallItemClass.Login);
        Assert.Equal(secretKey, loginItem.Description);
        Assert.Contains("sk-INVARIANT-LEAK", json);

        // ...while the genuinely-redacted channels stay OUT — proving this is a NARROW, named exception, not a
        // blanket hole: the probe path, sign-in command, and URL token are still fully redacted.
        Assert.DoesNotContain(".creds", json);
        Assert.DoesNotContain("SUPERSECRET", json);
        Assert.DoesNotContain("URLSECRET", json);
        Assert.DoesNotContain("authProbe", json);
        Assert.DoesNotContain("authCommand", json);
    }

    // ====================================================================================================
    // 7) CLASSIFICATION CONFLICT: an entry that has BOTH a non-empty AuthKey AND a winget id. FromManual checks
    //    AuthKey FIRST → it must classify as Login and carry ONLY the key (winget id suppressed to null).
    // ====================================================================================================

    [Fact]
    public void Manual_entry_with_both_authKey_and_wingetId_classifies_as_Login_and_suppresses_the_wingetId()
    {
        var entry = new InstallEntry(
            "both", "install", "ai-cli", InstallMethod.Winget,
            "Git.Git", null, false, false, 100, "desc")
        {
            InstallTier = InstallTier.ManualAfter,
            AuthKey = "claudekey",
        };
        InstallPlanExportDoc doc = InstallPlanExport.Build(ManualOnly(entry), new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        Assert.Equal(InstallItemClass.Login, item.Class);
        Assert.Null(item.WingetId);                 // Login suppresses package fields by construction
        Assert.Null(item.NpmPackage);
        Assert.Equal("claudekey", item.Description); // ONLY the key
    }

    // ====================================================================================================
    // 8) ORDERING TIE: two items with the SAME RestoreOrder must break the tie by EntryId (Ordinal). Pin the
    //    determinism so a hostile manifest cannot make the export order non-deterministic.
    // ====================================================================================================

    [Fact]
    public void Items_with_the_same_restore_order_break_the_tie_by_ordinal_entry_id()
    {
        var emptyPlan = new OperationPlan("Reinstall apps and restore settings", "install",
            Array.Empty<PlannedAction>(), T0);
        // Three skips, all RestoreOrder 5, ids deliberately out of order.
        var result = new InstallPlanResult(
            emptyPlan,
            new[]
            {
                new InstallSkip(RawEntry("mmm", InstallMethod.Winget, order: 5), InstallSkipReason.Incomplete, "n"),
                new InstallSkip(RawEntry("Aaa", InstallMethod.Winget, order: 5), InstallSkipReason.Incomplete, "n"),
                new InstallSkip(RawEntry("zzz", InstallMethod.Winget, order: 5), InstallSkipReason.Incomplete, "n"),
            },
            Array.Empty<InstallEntry>());

        InstallPlanExportDoc doc = InstallPlanExport.Build(result, new FakeClock(T0));

        // Ordinal sort: uppercase 'A' (0x41) sorts before lowercase 'm','z'.
        Assert.Equal(new[] { "Aaa", "mmm", "zzz" }, doc.Items.Select(i => i.EntryId).ToArray());
    }

    // ====================================================================================================
    // 9) DEDUP: an entry surfaced on the manual checklist must NOT be duplicated from the skip list, even when
    //    the skip carries a DIFFERENT reason. The dedup key is entry.Id. Verify single emission + manual wins.
    // ====================================================================================================

    [Fact]
    public void Entry_on_manual_checklist_is_not_duplicated_from_the_skip_list_even_with_a_different_reason()
    {
        var entry = RawEntry("dup", InstallMethod.UrlManual, order: 50);
        var emptyPlan = new OperationPlan("Reinstall apps and restore settings", "install",
            Array.Empty<PlannedAction>(), T0);
        var result = new InstallPlanResult(
            emptyPlan,
            new[] { new InstallSkip(entry, InstallSkipReason.GateBlocked, "blocked") },  // skip says GateBlocked
            new[] { entry });                                                            // also on manual checklist

        InstallPlanExportDoc doc = InstallPlanExport.Build(result, new FakeClock(T0));

        InstallPlanItem only = Assert.Single(doc.Items, i => i.EntryId == "dup");
        Assert.Equal(InstallItemClass.ManualUrl, only.Class);   // manual classification wins; skip is suppressed
        Assert.Null(only.SkipReason);                            // not the Excluded reason
        Assert.Single(doc.Items);                                // exactly one row total
    }

    // ====================================================================================================
    // 10) ALLOW-LIST REGEX BOUNDARY: a value that "looks valid" but is path-like. Backslash/colon/slash/space
    //     are all outside [A-Za-z0-9.+_-], so a Windows path can NEVER be allow-list-valid. Probe the edges:
    //     leading dash (flag smuggling), trailing/leading dot, plus/underscore, and a UNC-looking value.
    // ====================================================================================================

    [Theory]
    [InlineData("-Evil.Flag")]            // leading '-' would be read as a winget flag → must be rejected
    [InlineData(@"\\server\share")]       // UNC path
    [InlineData("a b")]                   // embedded space
    [InlineData("a/b")]                   // forward slash
    [InlineData("a\\b")]                  // backslash
    [InlineData("a:b")]                   // colon (drive/ADS)
    [InlineData("a;b")]                   // shell separator
    [InlineData("a&b")]                   // shell ampersand
    [InlineData("a|b")]                   // pipe
    [InlineData("$(evil)")]               // command substitution shape
    public void Excluded_path_or_flag_shaped_wingetId_is_always_dropped_to_null(string hostile)
    {
        var entry = RawEntry("edge", InstallMethod.Winget, wingetId: hostile);
        InstallPlanExportDoc doc = InstallPlanExport.Build(SkipOnly(entry), new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        Assert.Null(item.WingetId);
    }

    [Theory]
    [InlineData(".LeadingDot")]           // shape-valid: '.' is in-class and the first char '.'? NO — first char must be alnum
    [InlineData("Trailing.Dot.")]         // shape-valid
    [InlineData("With+Plus")]             // shape-valid
    [InlineData("With_Underscore")]       // shape-valid
    [InlineData("With-Dash")]             // shape-valid (dash not leading)
    public void Excluded_wingetId_edge_shapes_are_handled_consistently_with_the_planner_allow_list(string candidate)
    {
        var entry = RawEntry("edge2", InstallMethod.Winget, wingetId: candidate);
        InstallPlanExportDoc doc = InstallPlanExport.Build(SkipOnly(entry), new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        // The export's allow-list MUST agree 1:1 with the planner's IsValidWingetId (single source of truth).
        bool plannerSaysValid = InstallPlanner.IsValidWingetId(candidate);
        if (plannerSaysValid)
            Assert.Equal(candidate.Trim(), item.WingetId);
        else
            Assert.Null(item.WingetId);
    }

    // ====================================================================================================
    // 11) GATE-BLOCK WRITE PREVENTION: a write to a protected/system root must be refused and write NOTHING,
    //     even when the document itself is benign. (Mirrors the production refusal path through the runner.)
    // ====================================================================================================

    [Fact]
    public void Runner_refuses_a_protected_destination_and_writes_no_install_plan_file()
    {
        var entry = RawEntry("git", InstallMethod.Winget, wingetId: "Git.Git");
        InstallPlanResult plan = SkipOnly(entry);

        var runner = new InstallRunner(new InstallPlanWriter(), new FakeClock(T0));
        string evilRoot = @"C:\Windows\System32\wck-redteam";
        InstallRunResult result = runner.ExportPlan(plan, evilRoot, RealGate());

        Assert.False(result.Authorized);
        Assert.False(File.Exists(Path.Combine(evilRoot, InstallPlanFiles.Plan)));
    }

    // ====================================================================================================
    // 12) METHOD TOKEN SMUGGLING on the manual/skip channel: a raw path-shaped 'method' must be dropped to "".
    //     (FromManual/Excluded run method through AllowedMethod.) Also verify a CASE-variant of a known method
    //     token: AllowedMethod uses Ordinal contains, so "Install-Winget" (wrong case) is NOT a known token →
    //     dropped to "". Pin the exact behavior.
    // ====================================================================================================

    [Fact]
    public void Excluded_path_shaped_method_is_dropped_to_empty_string()
    {
        var entry = RawEntry("m", @"C:\Users\victim\evil.exe", wingetId: null);
        InstallPlanExportDoc doc = InstallPlanExport.Build(SkipOnly(entry), new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        Assert.Equal(string.Empty, item.Method);
        Assert.DoesNotContain("victim", Serialize(doc));
        Assert.DoesNotContain("evil.exe", Serialize(doc));
    }

    [Fact]
    public void Excluded_case_variant_method_token_is_dropped_to_empty_string_ordinal_match()
    {
        // AllowedMethod uses an Ordinal HashSet → "Install-Winget" (different case) is NOT a member → "".
        var entry = RawEntry("case", "Install-Winget");
        InstallPlanExportDoc doc = InstallPlanExport.Build(SkipOnly(entry), new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        Assert.Equal(string.Empty, item.Method);
    }

    // ====================================================================================================
    // 13) PARTIAL-MATCH winget id: the regex is fully anchored (^...$). A value with a valid PREFIX but an
    //     illegal suffix (or vice-versa) must NOT partial-match. Probe "Git.Git<space>EVIL" and "EVIL Git.Git".
    // ====================================================================================================

    [Theory]
    [InlineData("Git.Git EVIL")]
    [InlineData("EVIL Git.Git")]
    [InlineData("Git.Git\tEVIL")]
    [InlineData("Git.Git\0EVIL")]   // embedded NUL
    public void Excluded_wingetId_with_a_valid_prefix_but_illegal_suffix_is_fully_rejected(string hostile)
    {
        var entry = RawEntry("partial", InstallMethod.Winget, wingetId: hostile);
        InstallPlanExportDoc doc = InstallPlanExport.Build(SkipOnly(entry), new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        Assert.Null(item.WingetId);
        Assert.DoesNotContain("EVIL", Serialize(doc));
    }

    // ====================================================================================================
    // 14) FULL WHOLE-DOCUMENT serialize through the REAL writer for a multi-class hostile manifest, asserting
    //     the serialized file on disk (under %TEMP%) carries none of the per-field markers EXCEPT the two
    //     by-contract channels (EntryId here is kept SAFE so the test isolates the field-level redaction).
    // ====================================================================================================

    [Fact]
    public void WriteExport_real_writer_leaks_no_marker_for_a_multiclass_hostile_manifest_with_safe_ids()
    {
        using var ws = new TempWorkspace("wck-redteam-multiclass-");

        var emptyPlan = new OperationPlan("Reinstall apps and restore settings", "install",
            Array.Empty<PlannedAction>(), T0);

        // SAFE entry ids (so EntryId/description do not themselves leak); every OTHER raw field is hostile.
        var manual = new InstallEntry(
            "mlogin", "install", "ai-cli", InstallMethod.UrlManual,
            @"C:\Users\victim\wSECRET", @"C:\Users\victim\nSECRET", false, false, 10,
            @"Sign in; creds at C:\Users\victim\DESCSECRET")
        {
            InstallTier = InstallTier.ManualAfter,
            AuthKey = "shortkey",
            AuthProbe = @"C:\Users\victim\PROBESECRET",
            AuthCommand = "login --secret AUTHCMDSECRET",
            ManualUrl = "https://evil.test/login?token=URLSECRET",
        };
        var skip = new InstallEntry(
            "mskip", "install", "ai-cli", @"C:\Users\victim\METHODSECRET",
            @"C:\Users\victim\wSECRET2", @"C:\Users\victim\nSECRET2", false, false, 20, "skipdesc");

        var result = new InstallPlanResult(
            emptyPlan,
            new[] { new InstallSkip(skip, InstallSkipReason.Incomplete, "https://dl.evil.test/x?key=NOTESECRET") },
            new[] { manual });

        InstallPlanExportDoc doc = InstallPlanExport.Build(result, new FakeClock(T0));
        string path = new InstallPlanWriter().WriteExport(doc, ws.Root, RealGate());
        string json = File.ReadAllText(path);

        foreach (string marker in new[]
        {
            "wSECRET", "nSECRET", "DESCSECRET", "PROBESECRET", "AUTHCMDSECRET", "URLSECRET",
            "METHODSECRET", "wSECRET2", "nSECRET2", "NOTESECRET",
            "C:\\Users", @"C:\Users", "victim", "evil.test", ".credentials",
        })
        {
            Assert.DoesNotContain(marker, json);
        }

        Assert.DoesNotContain("authProbe", json);
        Assert.DoesNotContain("authCommand", json);
        Assert.DoesNotContain("\"manualUrl\":", json);

        // The one safe by-contract value (the short auth key) DID travel.
        Assert.Contains("shortkey", json);
    }

    // ====================================================================================================
    // 15) DUPLICATE ENTRY IDS on the manual checklist (two DIFFERENT entries sharing an Id). FromManual is run
    //     per checklist entry with no de-dup among manual entries themselves → both are emitted. Document the
    //     resulting count so any future de-dup change is caught.
    // ====================================================================================================

    [Fact]
    public void Two_manual_entries_sharing_an_id_both_surface_documenting_no_intra_manual_dedup()
    {
        var a = RawEntry("samestable", InstallMethod.UrlManual, order: 10);
        var b = RawEntry("samestable", InstallMethod.UrlManual, order: 20);
        var emptyPlan = new OperationPlan("Reinstall apps and restore settings", "install",
            Array.Empty<PlannedAction>(), T0);
        var result = new InstallPlanResult(emptyPlan, Array.Empty<InstallSkip>(), new[] { a, b });

        InstallPlanExportDoc doc = InstallPlanExport.Build(result, new FakeClock(T0));

        // The manual channel has no intra-list de-dup: both entries surface. (Only the skip→manual overlap is
        // de-duped.) Pin the current behavior so a future change is intentional, not accidental.
        Assert.Equal(2, doc.Items.Count(i => i.EntryId == "samestable"));
    }

    // ====================================================================================================
    // 16) CONFIG (Copy) PATH never leaks — the RestoreMergeAction carries Source/Destination paths on the
    //     action record, and the planner's UI text embeds the destination. The export must build the label from
    //     SafeLabel(entryId), never from the action's Description/Destination. Use a SkipOnly Copy via a direct
    //     RestoreMergeAction so the (gate-independent) Copy branch is exercised, plus a planner-built config that
    //     gets gate-blocked to a victim profile path — BOTH must surface zero path material in the item/JSON.
    // ====================================================================================================

    [Fact]
    public void Copy_item_from_a_restore_merge_action_never_carries_the_source_or_destination_path()
    {
        // A RestoreMergeAction whose Source/Destination + UI Description are all profile paths, with an entry
        // mapping so it is classified as Copy (the gate is NOT consulted by Build — this isolates the Copy label).
        var restore = new RestoreMergeAction
        {
            Source = @"C:\Users\victim\AppData\Roaming\App\settings.json",
            Destination = @"C:\Users\victim\AppData\Roaming\App\restored.json",
            Description = @"Restore config to C:\Users\victim\AppData\Roaming\App\restored.json",
            Reason = "r",
        };
        var plan = new OperationPlan("Reinstall apps and restore settings", "install", new[] { restore }, T0);
        var result = new InstallPlanResult(plan, Array.Empty<InstallSkip>(), Array.Empty<InstallEntry>())
        {
            ActionEntryIds = new Dictionary<string, string> { [restore.Id] = "cfg" },
            RestoreOrderByEntryId = new Dictionary<string, int> { ["cfg"] = 100 },
        };

        InstallPlanExportDoc doc = InstallPlanExport.Build(result, new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        Assert.Equal(InstallItemClass.Copy, item.Class);
        Assert.Equal("cfg", item.EntryId);
        Assert.DoesNotContain(@"C:\Users", item.Description);
        Assert.DoesNotContain("victim", item.Description);
        Assert.DoesNotContain("restored.json", item.Description);

        string json = Serialize(doc);
        Assert.DoesNotContain("victim", json);
        Assert.DoesNotContain("AppData", json);
        Assert.DoesNotContain("settings.json", json);
        Assert.DoesNotContain("restored.json", json);
    }

    [Fact]
    public void Config_restore_to_a_foreign_profile_is_gate_blocked_and_still_leaks_no_path()
    {
        // The real planner gate refuses a config-restore destination under another user's profile → it becomes an
        // Excluded skip. Whatever the class, NO source/destination path may reach the item or the JSON.
        const string src = @"C:\Users\victim\AppData\Roaming\App\settings.json";
        const string dst = @"C:\Users\victim\AppData\Roaming\App\restored.json";

        var entry = new InstallEntry(
            "cfg", "install", "config", InstallMethod.ConfigRestore,
            null, null, false, false, 100, @"restore to C:\Users\victim\App")
        { ConfigSource = src, ConfigDestination = dst };

        InstallPlanResult plan = new InstallPlanner(RealGate(), new AllNet())
            .BuildPlan(new InstallManifest(new[] { entry }), RestoreState.Empty, T0);

        InstallPlanExportDoc doc = InstallPlanExport.Build(plan, new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        Assert.DoesNotContain("victim", item.Description);
        Assert.DoesNotContain("restored.json", item.Description);

        string json = Serialize(doc);
        Assert.DoesNotContain("victim", json);
        Assert.DoesNotContain("AppData", json);
        Assert.DoesNotContain("settings.json", json);
        Assert.DoesNotContain("restored.json", json);
    }

    // ====================================================================================================
    // 17) The Copy branch falls back to action.Id (a GUID) when the action→entry map is missing — it must NEVER
    //     reach into the action's path-bearing Description/Destination. Build a RestoreMergeAction directly with
    //     NO map entry and assert the path never surfaces (only a GUID-shaped entry id does).
    // ====================================================================================================

    [Fact]
    public void Copy_item_with_a_missing_action_entry_map_falls_back_to_the_action_id_not_the_path()
    {
        var restore = new RestoreMergeAction
        {
            Source = @"C:\Users\victim\src.json",
            Destination = @"C:\Users\victim\dst.json",
            Description = "Restore config to C:\\Users\\victim\\dst.json",
            Reason = "r",
        };
        var plan = new OperationPlan("Reinstall apps and restore settings", "install", new[] { restore }, T0);
        // No ActionEntryIds / RestoreOrderByEntryId map at all → the export must fall back to action.Id.
        var result = new InstallPlanResult(plan, Array.Empty<InstallSkip>(), Array.Empty<InstallEntry>());

        InstallPlanExportDoc doc = InstallPlanExport.Build(result, new FakeClock(T0));

        InstallPlanItem item = Assert.Single(doc.Items);
        Assert.Equal(InstallItemClass.Copy, item.Class);
        Assert.Equal(restore.Id, item.EntryId);          // fell back to the GUID action id
        Assert.DoesNotContain("victim", Serialize(doc)); // the path never travels
    }

    // ====================================================================================================
    // 18) CLOCK determinism: GeneratedUtc comes from the injected clock, never from the wall clock. Two builds
    //     with the same FakeClock produce the same stamp; a different clock changes only the stamp.
    // ====================================================================================================

    [Fact]
    public void GeneratedUtc_is_taken_from_the_injected_clock_only()
    {
        var entry = RawEntry("git", InstallMethod.Winget, wingetId: "Git.Git");
        InstallPlanResult plan = SkipOnly(entry);

        var t1 = new DateTime(2030, 5, 5, 1, 2, 3, DateTimeKind.Utc);
        InstallPlanExportDoc a = InstallPlanExport.Build(plan, new FakeClock(T0));
        InstallPlanExportDoc b = InstallPlanExport.Build(plan, new FakeClock(t1));

        Assert.Equal(T0, a.GeneratedUtc);
        Assert.Equal(t1, b.GeneratedUtc);
        Assert.Equal(InstallPlanExport.SchemaVersion, a.SchemaVersion);
    }

    /// <summary>A driver guard that approves every identifier (so a driver entry is not skipped on class).</summary>
    private sealed class AllNet : IDriverGuard
    {
        public bool IsNetClass(string driverIdentifier) => true;
    }
}

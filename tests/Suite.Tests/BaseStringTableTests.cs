using System.IO;
using WindowsCareKit.App.Deployment;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Startup;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// The mandatory English string table's one contract, and the reason it is only one.
/// <para>
/// The shipped defect was a disagreement: the startup check proved "parses as JSON, root is an object" while
/// the live loader needed <c>Dictionary&lt;string,string&gt;</c> and swallowed its own failure into an empty
/// table — so a file with a numeric value passed the gate and produced a UI of raw i18n keys. These tests pin
/// the shared rule, and pin that the two callers still agree.
/// </para>
/// <para>
/// Agreement was necessary and not sufficient: both callers then agreed that <c>{}</c> was fine, which
/// reproduced the very same raw-key UI with a clean exit code in front of it. The second theory below pins
/// the required-key floor that closed it, through the same two callers.
/// </para>
/// </summary>
public sealed class BaseStringTableTests
{
    private const string AppDir = @"C:\Program Files\WindowsCareKit";
    private const string AppExe = @"C:\Program Files\WindowsCareKit\WindowsCareKit.exe";
    private static readonly string BaseTablePath = Path.Combine(AppDir, "lang", "en.json");

    /// <summary>Every payload the SHAPE rule has an opinion about, and whether it is usable. This one list
    /// drives BOTH callers below — the agreement is asserted, not assumed.</summary>
    public static TheoryData<string, bool> ShapePayloads => new()
    {
        { /*lang=json,strict*/ """{ }""", false },                      // parses, and says nothing: the BLOCKER
        { /*lang=json,strict*/ """{ "meta.languageName": "English" }""", false }, // non-empty, and I18n strips it back to empty
        { /*lang=json,strict*/ """{ "app.title": 123 }""", false },     // THE shipped defect: object root, wrong value
        { /*lang=json,strict*/ """{ "app.title": null }""", false },
        { /*lang=json,strict*/ """{ "app.title": true }""", false },
        { /*lang=json,strict*/ """{ "app.title": [ "a" ] }""", false },
        { /*lang=json,strict*/ """{ "app.title": { "a": "b" } }""", false },
        { /*lang=json,strict*/ """{ "ok": "yes", "bad": 1 }""", false },// one bad value spoils the table
        { "[1, 2, 3]", false },
        { "\"a string\"", false },
        { "null", false },
        { "", false },
        { "not json at all", false },
    };

    /// <summary>The required-key floor, one deliberate defect at a time against an otherwise complete table.
    /// The case name is carried so the theory reads as cases rather than as walls of JSON.</summary>
    public static TheoryData<string, string, bool> RequiredKeyPayloads => new()
    {
        { "complete", BaseTableFixture.JsonTitled(), true },
        { "first required key absent", BaseTableFixture.JsonWithout("app.title"), false },
        { "last required key absent", BaseTableFixture.JsonWithout("plan.risk.Critical"), false },
        { "required key present but blank", BaseTableFixture.JsonWithBlank("confirm.approve"), false },
    };

    [Theory]
    [MemberData(nameof(ShapePayloads))]
    public void The_startup_gate_and_the_live_loader_accept_exactly_the_same_base_tables(
        string payload, bool usable)
        => AssertBothCallersAgree(payload, usable);

    [Theory]
    [MemberData(nameof(RequiredKeyPayloads))]
    public void A_table_missing_or_blanking_any_key_the_shell_renders_is_refused_by_both_callers(
        string caseName, string payload, bool usable)
    {
        Assert.NotEmpty(caseName); // carried for the test display name; asserted so it is not dead weight
        AssertBothCallersAgree(payload, usable);
    }

    /// <summary>Runs one payload through both callers of the shared rule and pins that they answer alike.</summary>
    private static void AssertBothCallersAgree(string payload, bool usable)
    {
        // Caller 1: the pre-startup gate, over injected content.
        StartupPreflightResult preflight = StartupPreflight.Run(
            AppLayout.Create(AppExe, null, AppDir, (p, _) => p.Equals(BaseTablePath, StringComparison.OrdinalIgnoreCase)),
            _ => payload);

        // Caller 2: the live table the UI actually binds to, over a real file.
        using var ws = new TempWorkspace("wck-basetable-");
        ws.WriteFile("en.json", payload);
        var i18n = new I18n(ws.Root);
        BaseStringTableResult? liveFailure = null;
        i18n.BaseStringTableUnusable += (_, failure) => liveFailure = failure;
        i18n.Load("en");

        Assert.Equal(usable, preflight.IsOk);
        Assert.Equal(usable, liveFailure is null);

        // Stated as the invariant itself: if these two ever diverge again, this line is what goes red.
        Assert.Equal(preflight.IsOk, liveFailure is null);
    }

    [Fact]
    public void An_empty_table_is_refused_as_unreadable_and_the_reason_names_what_is_missing()
    {
        // The exit code the release gate reads comes from this status, and the detail is what a maintainer
        // has to act on: "it is empty" is a diagnosis, "0 of 61" with names is a repair instruction.
        BaseStringTableResult empty = BaseStringTable.Parse(BaseTablePath, "{}");

        Assert.Equal(BaseStringTableStatus.Unreadable, empty.Status);
        Assert.Contains("app.title", empty.FailureDetail!, StringComparison.Ordinal);
        Assert.Contains(
            BaseStringTable.RequiredKeys.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            empty.FailureDetail!,
            StringComparison.Ordinal);
    }


    [Fact]
    public void A_base_table_deleted_after_a_good_load_keeps_the_live_strings_instead_of_going_empty()
    {
        using var ws = new TempWorkspace("wck-basetable-");
        ws.WriteFile("en.json", BaseTableFixture.JsonTitled());
        ws.WriteFile("tr.json", /*lang=json,strict*/ """{ "app.title": "Windows Bakım Kiti" }""");

        var i18n = new I18n(ws.Root);
        i18n.Load("en");
        Assert.Equal("Windows Care Kit", i18n["app.title"]);

        File.Delete(Path.Combine(ws.Root, "en.json"));
        BaseStringTableResult? failure = null;
        i18n.BaseStringTableUnusable += (_, f) => failure = f;

        i18n.Load("tr"); // the live language switch, with the base file gone

        Assert.NotNull(failure);
        Assert.Equal(BaseStringTableStatus.Missing, failure!.Status);
        Assert.NotNull(failure.FailureDetail);
        Assert.Equal(Path.Combine(ws.Root, "en.json"), failure.FilePath);

        // The point of the whole exercise: the UI still reads.
        Assert.Equal("Windows Care Kit", i18n["app.title"]);
        Assert.NotEmpty(i18n.Map);
        // ...and the switch honestly did not happen, so the bound selector is told to snap back.
        Assert.Equal("en", i18n.Culture);
        Assert.Equal("en", i18n.SelectedCulture);
    }

    [Fact]
    public void A_base_table_corrupted_after_a_good_load_keeps_the_live_strings_instead_of_going_empty()
    {
        using var ws = new TempWorkspace("wck-basetable-");
        ws.WriteFile("en.json", BaseTableFixture.JsonTitled());

        var i18n = new I18n(ws.Root);
        i18n.Load("en");

        ws.WriteFile("en.json", /*lang=json,strict*/ """{ "app.title": 123 }"""); // the exact shipped case
        BaseStringTableResult? failure = null;
        i18n.BaseStringTableUnusable += (_, f) => failure = f;

        i18n.Load("en");

        Assert.NotNull(failure);
        Assert.Equal(BaseStringTableStatus.Unreadable, failure!.Status);
        Assert.Equal("Windows Care Kit", i18n["app.title"]); // NOT the raw key
    }

    [Fact]
    public void A_broken_NON_english_overlay_is_still_tolerated_and_falls_back_to_english()
    {
        // The strictness is deliberately asymmetric. An overlay has English to fall back TO; the base has
        // nothing, which is why only the base is fatal. This pins the tolerant half so it is not "fixed" too.
        using var ws = new TempWorkspace("wck-basetable-");
        ws.WriteFile("en.json", BaseTableFixture.JsonTitled());
        ws.WriteFile("xx.json", "{ this is not valid json ");

        var i18n = new I18n(ws.Root);
        bool announced = false;
        i18n.BaseStringTableUnusable += (_, _) => announced = true;

        i18n.Load("xx");

        Assert.False(announced);
        Assert.Equal("xx", i18n.Culture);                    // the switch happened
        Assert.Equal("Windows Care Kit", i18n["app.title"]);  // with English strings underneath
    }

    [Fact]
    public void An_unloaded_result_refuses_to_hand_out_an_empty_table()
    {
        // The fabrication this type exists to prevent must be impossible, not merely discouraged.
        BaseStringTableResult broken = BaseStringTable.Parse(
            BaseTablePath, /*lang=json,strict*/ """{ "app.title": 123 }""");

        Assert.False(broken.IsLoaded);
        Assert.Throws<InvalidOperationException>(() => broken.Entries);
    }

    [Fact]
    public void A_load_failure_keeps_its_cause()
    {
        BaseStringTableResult denied = BaseStringTable.Load(
            BaseTablePath, _ => throw new UnauthorizedAccessException("access denied by ACL"));

        Assert.Equal(BaseStringTableStatus.Unreadable, denied.Status);
        Assert.Contains(nameof(UnauthorizedAccessException), denied.FailureDetail!, StringComparison.Ordinal);
        Assert.Contains("access denied by ACL", denied.FailureDetail!, StringComparison.Ordinal);
    }
}

using System.IO;
using WindowsCareKit.App.Deployment;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Startup;
using WindowsCareKit.Tests.TestInfra;
using Xunit;
using WpfApp = WindowsCareKit.App.App;

namespace WindowsCareKit.Tests;

/// <summary>
/// The check that gates startup before any module (i.e. any third-party code) is loaded. Everything here is
/// injected — a fake layout and a fake reader — so the whole matrix runs headlessly.
/// </summary>
public sealed class StartupPreflightTests
{
    private const string AppDir = @"C:\Program Files\WindowsCareKit";
    private const string AppExe = @"C:\Program Files\WindowsCareKit\WindowsCareKit.exe";
    private const string LinkDir = @"C:\Users\u\AppData\Local\Microsoft\WinGet\Links";
    private const string LinkExe = @"C:\Users\u\AppData\Local\Microsoft\WinGet\Links\WindowsCareKit.exe";

    private static readonly string BaseTablePath = Path.Combine(AppDir, "lang", "en.json");
    private static readonly string ModulesPath = Path.Combine(AppDir, "Modules");
    private static readonly string ManifestsPath = Path.Combine(AppDir, "manifests");

    /// <summary>A base table that clears the production floor — a one-key stand-in is not a shipped table
    /// and, since the floor exists, is not a passing one either.</summary>
    private static readonly string ValidTable = BaseTableFixture.JsonTitled();

    /// <summary>A determined layout whose filesystem contains exactly <paramref name="present"/>, each as the
    /// kind the app requires it to be.</summary>
    private static AppLayout LayoutWith(params string[] present) =>
        AppLayout.Create(AppExe, null, AppDir, (p, _) => present.Contains(p, StringComparer.OrdinalIgnoreCase));

    /// <summary>A determined layout where the named paths exist but as the WRONG kind of filesystem entry.</summary>
    private static AppLayout LayoutWhereTheseAreTheWrongKind(params string[] wrongKind) =>
        AppLayout.Create(AppExe, null, AppDir, (p, kind) =>
            wrongKind.Contains(p, StringComparer.OrdinalIgnoreCase)
                ? kind == AppResourceKind.File // present as a regular file; every optional resource wants a folder
                : true);

    private static AppLayout LinkLaunchedLayout() =>
        AppLayout.Create(LinkExe, AppExe, LinkDir, (_, _) => true);

    private static Func<string, string> Reads(string content) => _ => content;

    [Fact]
    public void A_complete_install_passes_and_exits_zero()
    {
        StartupPreflightResult result = StartupPreflight.Run(
            LayoutWith(BaseTablePath, ModulesPath, ManifestsPath), Reads(ValidTable));

        Assert.True(result.IsOk);
        Assert.Equal(StartupPreflight.ExitOk, result.ExitCode);
        Assert.Contains("status=Ok", result.ToReportLine(), StringComparison.Ordinal);
        Assert.Contains(StartupPreflight.TokenOptionalPresent, result.ToReportLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void Absent_modules_and_manifests_are_a_valid_install_not_a_failure()
    {
        // The installer's supported "Base only" compact type ships neither directory
        // (installer/WindowsCareKit.iss:48-51,64-65). Their absence must never stop the app.
        StartupPreflightResult result = StartupPreflight.Run(LayoutWith(BaseTablePath), Reads(ValidTable));

        Assert.True(result.IsOk);
        Assert.Equal(StartupPreflight.ExitOk, result.ExitCode);

        string line = result.ToReportLine();
        Assert.Contains("Modules=" + StartupPreflight.TokenOptionalAbsent, line, StringComparison.Ordinal);
        Assert.Contains("manifests=" + StartupPreflight.TokenOptionalAbsent, line, StringComparison.Ordinal);
        Assert.DoesNotContain(StartupPreflight.TokenMissing, line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_base_string_table_is_fatal_and_names_the_path()
    {
        StartupPreflightResult result = StartupPreflight.Run(
            LayoutWith(ModulesPath, ManifestsPath), Reads(ValidTable));

        Assert.Equal(StartupPreflightStatus.BaseStringTableMissing, result.Status);
        Assert.Equal(StartupPreflight.ExitResourcesUnusable, result.ExitCode);
        Assert.Equal(new[] { BaseStringTable.RelativePath }, result.MissingRequired);

        Assert.Contains(BaseTablePath, result.ToFatalMessage(), StringComparison.Ordinal);
        Assert.Contains(
            BaseStringTable.RelativePath + "=" + StartupPreflight.TokenMissing,
            result.ToReportLine(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]      // valid JSON, wrong shape — a table is an object
    [InlineData("\"a string\"")]
    [InlineData("")]
    [InlineData("null")]                                  // parses, but there is no table
    [InlineData(/*lang=json,strict*/ """{ "app.title": 123 }""")]              // object root, value not a string
    [InlineData(/*lang=json,strict*/ """{ "app.title": null }""")]             // ditto: null is not a string
    [InlineData(/*lang=json,strict*/ """{ "app.title": { "a": "b" } }""")]     // ditto: nested object
    [InlineData(/*lang=json,strict*/ """{ "app.title": true }""")]             // ditto: boolean
    [InlineData(/*lang=json,strict*/ """{ }""")]                               // the shape is fine and it says nothing
    [InlineData(/*lang=json,strict*/ """{ "meta.languageName": "English" }""")] // non-empty, but I18n strips it to empty
    public void A_present_but_broken_base_string_table_is_fatal_too(string content)
    {
        // Without this branch the I18n swallow turns a corrupt table into an EMPTY one, and the UI renders
        // every label as its raw key — the shipped v0.1.2-beta defect, with the file right there.
        // The non-string VALUE cases are the ones an object-root-only check let through: it said Ok, and the
        // live loader then failed on the very same file and fabricated an empty table.
        // The last two cases are the ones a shape-only check let through: nothing is malformed, there is
        // simply nothing to render, and an app started on either would BE the raw-key UI.
        StartupPreflightResult result = StartupPreflight.Run(LayoutWith(BaseTablePath), Reads(content));

        Assert.Equal(StartupPreflightStatus.BaseStringTableUnreadable, result.Status);
        Assert.Equal(StartupPreflight.ExitResourcesUnusable, result.ExitCode);
        Assert.NotNull(result.Detail);
        Assert.Contains(BaseTablePath, result.ToFatalMessage(), StringComparison.Ordinal);
        Assert.Contains(
            BaseStringTable.RelativePath + "=" + StartupPreflight.TokenUnreadable,
            result.ToReportLine(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_base_table_that_vanishes_between_the_existence_check_and_the_read_reports_missing()
    {
        // The layout says it is there; the read then says it is not. "Absent" survives that race instead of
        // being flattened into the generic unreadable bucket, because it names a different repair.
        StartupPreflightResult result = StartupPreflight.Run(
            LayoutWith(BaseTablePath), _ => throw new FileNotFoundException("deleted mid-startup", BaseTablePath));

        Assert.Equal(StartupPreflightStatus.BaseStringTableMissing, result.Status);
        Assert.Equal(StartupPreflight.ExitResourcesUnusable, result.ExitCode);
        Assert.Equal(new[] { BaseStringTable.RelativePath }, result.MissingRequired);
    }

    [Fact]
    public void A_regular_file_named_like_an_optional_folder_is_not_reported_present()
    {
        // Modules\ and manifests\ are enumerated as directories. A regular file of that name provides no
        // capability at all, so a generic "the path exists" answer would claim an install feature that is
        // simply not there.
        StartupPreflightResult result = StartupPreflight.Run(
            LayoutWhereTheseAreTheWrongKind(ModulesPath, ManifestsPath), Reads(ValidTable));

        string line = result.ToReportLine();
        Assert.True(result.IsOk); // still a valid (compact-shaped) install — this is not a fatal condition
        Assert.Contains("Modules=" + StartupPreflight.TokenOptionalAbsent, line, StringComparison.Ordinal);
        Assert.Contains("manifests=" + StartupPreflight.TokenOptionalAbsent, line, StringComparison.Ordinal);
        Assert.DoesNotContain(StartupPreflight.TokenOptionalPresent, line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_base_string_table_keeps_the_cause()
    {
        StartupPreflightResult result = StartupPreflight.Run(
            LayoutWith(BaseTablePath),
            _ => throw new UnauthorizedAccessException("access denied by ACL"));

        Assert.Equal(StartupPreflightStatus.BaseStringTableUnreadable, result.Status);
        Assert.Contains(nameof(UnauthorizedAccessException), result.Detail!, StringComparison.Ordinal);
        Assert.Contains("access denied by ACL", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_undetermined_layout_is_fatal_and_reports_both_candidate_paths()
    {
        StartupPreflightResult result = StartupPreflight.Run(LinkLaunchedLayout(), Reads(ValidTable));

        Assert.Equal(StartupPreflightStatus.LayoutUndetermined, result.Status);
        Assert.Equal(StartupPreflight.ExitLayoutUndetermined, result.ExitCode);

        string message = result.ToFatalMessage();
        Assert.Contains(AppDir, message, StringComparison.Ordinal);
        Assert.Contains(LinkDir, message, StringComparison.Ordinal);

        string line = result.ToReportLine();
        Assert.Contains("status=LayoutUndetermined", line, StringComparison.Ordinal);
        Assert.Contains("root=\"<none>\"", line, StringComparison.Ordinal);
        // Nothing is claimed about resources under a root we could not establish.
        Assert.Contains(StartupPreflight.TokenUnknown, line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_is_always_a_single_line()
    {
        // A preserved JsonException message is multi-line; the machine-readable contract is one line.
        foreach (StartupPreflightResult result in new[]
        {
            StartupPreflight.Run(LayoutWith(BaseTablePath, ModulesPath, ManifestsPath), Reads(ValidTable)),
            StartupPreflight.Run(LayoutWith(BaseTablePath), Reads("{ oops\r\n  bad }")),
            StartupPreflight.Run(LayoutWith(), Reads(ValidTable)),
            StartupPreflight.Run(LinkLaunchedLayout(), Reads(ValidTable)),
        })
        {
            string line = result.ToReportLine();
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);
            Assert.StartsWith("WCK-LAYOUT ", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_fatal_message_is_hardcoded_english_not_a_string_table_lookup()
    {
        // If this text ever came from I18n it would be raw keys in exactly the situation it explains.
        string message = StartupPreflight.Run(LayoutWith(), Reads(ValidTable)).ToFatalMessage();

        Assert.Contains("Windows Care Kit cannot start", message, StringComparison.Ordinal);
        Assert.DoesNotContain("app.title", message, StringComparison.Ordinal);
        Assert.DoesNotContain("startup.", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "--verify-layout" }, true)]
    [InlineData(new[] { "--VERIFY-LAYOUT" }, true)]
    [InlineData(new[] { "--lang", "tr", "--verify-layout" }, true)]
    [InlineData(new[] { "--lang", "tr" }, false)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--verify-layout=1" }, false)] // a bare flag, not a --name=value option
    public void The_verify_switch_is_parsed_as_a_bare_flag(string[] args, bool expected)
    {
        Assert.Equal(expected, WpfApp.HasFlag(args, WpfApp.VerifyLayoutFlag));
    }

    [Fact]
    public void The_verify_flag_does_not_swallow_the_following_argument()
    {
        // ExtractOption would consume "--screen" as --verify-layout's value; a flag parser must not.
        string[] args = ["--verify-layout", "--screen", "backup"];

        Assert.True(WpfApp.HasFlag(args, WpfApp.VerifyLayoutFlag));
        Assert.Equal("backup", WpfApp.ExtractOption(args, "--screen"));
    }
}

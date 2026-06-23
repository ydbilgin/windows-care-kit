using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Uninstall;
using Xunit;

namespace WindowsCareKit.Tests.Migration.Detection;

/// <summary>
/// Non-vacuous tests for the RegistryUninstallSource / ProgramJoinKeys / ProgramDedupLayer /
/// ProgramDetector stack. All tests are host-safe (zero real IO; fake reader + fake canonicalizer).
/// </summary>
public class RegistryUninstallSourceTests
{
    // ── Test 1: Projection — GUID keyname → ProductCode; InstallLocation → leaf; DisplayName → NormalizedName ──

    [Fact]
    public void Projection_MSI_GUID_sets_product_code_and_leaf_and_normalized_name()
    {
        const string validGuid = "{12345678-1234-1234-1234-123456789ABC}";
        var app = MakeApp(
            displayName: "Foo App 1.2.3",
            keyName: validGuid,
            installLocation: @"C:\Program Files\Foo\",
            source: InstalledAppSource.MachineWide64);

        var source = new RegistryUninstallSource(FakeInstalledAppReader.With(app), FakeCanonicalizer.Instance);
        var result = source.Enumerate();

        Assert.Equal(ProgramSourceStatus.Ok, result.Report.Status);
        var prog = Assert.Single(result.Programs);

        // ProductCode must be lowercase form of the key name
        Assert.Equal(validGuid.ToLowerInvariant(), prog.ProductCode);

        // InstallPathLeaf: last segment of "C:\Program Files\Foo\" → "foo" (lowercase, trailing sep trimmed)
        Assert.Equal("foo", prog.InstallPathLeaf);

        // NormalizedName: "Foo App 1.2.3" → NFKC + casefold + strip trailing version → "foo app"
        Assert.Equal("foo app", prog.NormalizedName);
    }

    // ── Test 2: Scope mapping ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(InstalledAppSource.MachineWide64, ProgramScope.Machine)]
    [InlineData(InstalledAppSource.MachineWide32, ProgramScope.Machine)]
    [InlineData(InstalledAppSource.CurrentUser,   ProgramScope.CurrentUser)]
    public void Scope_maps_correctly(InstalledAppSource input, ProgramScope expected)
    {
        var app = MakeApp(displayName: "ScopeApp", keyName: "ScopeApp", source: input);
        var source = new RegistryUninstallSource(FakeInstalledAppReader.With(app), FakeCanonicalizer.Instance);
        var result = source.Enumerate();

        var prog = Assert.Single(result.Programs);
        Assert.Equal(expected, prog.Scope);
    }

    // ── Test 3: IsSystemComponent=true is NOT dropped, flag preserved ─────────────────────────────

    [Fact]
    public void System_component_is_not_dropped_and_flag_preserved()
    {
        var app = MakeApp(displayName: "SysComp", keyName: "SysComp", isSystemComponent: true);
        var source = new RegistryUninstallSource(FakeInstalledAppReader.With(app), FakeCanonicalizer.Instance);
        var result = source.Enumerate();

        var prog = Assert.Single(result.Programs);
        Assert.True(prog.IsSystemComponent);
    }

    // ── Test 4: Non-vacuous B.3 — empty list → SourceFailed ─────────────────────────────────────
    // REVERT TARGET: if the empty-list → SourceFailed branch were changed to Ok, this test fails.

    [Fact]
    public void Empty_reader_result_yields_SourceFailed_and_empty_programs()
    {
        var source = new RegistryUninstallSource(FakeInstalledAppReader.Empty(), FakeCanonicalizer.Instance);
        var result = source.Enumerate();

        Assert.Equal(ProgramSourceStatus.SourceFailed, result.Report.Status);
        Assert.Empty(result.Programs);
        Assert.Equal(0, result.Report.Count);
    }

    // ── Test 5: Reader throws → SourceFailed, no exception propagated ────────────────────────────

    [Fact]
    public void Reader_throws_yields_SourceFailed_without_propagating()
    {
        var source = new RegistryUninstallSource(FakeInstalledAppReader.Throwing(), FakeCanonicalizer.Instance);

        // Must not throw.
        var result = source.Enumerate();

        Assert.Equal(ProgramSourceStatus.SourceFailed, result.Report.Status);
        Assert.Empty(result.Programs);
    }

    // ── Test 6: Dedup — same ProductCode, two scopes → Machine wins, Sources union ───────────────

    [Fact]
    public void Dedup_same_ProductCode_merges_and_Machine_scope_wins()
    {
        const string guid = "{AABBCCDD-1111-2222-3333-444455556666}";
        var machine = MakeApp(
            displayName: "SharedApp",
            keyName: guid,
            source: InstalledAppSource.MachineWide64);
        var user = MakeApp(
            displayName: "SharedApp",
            keyName: guid,
            source: InstalledAppSource.CurrentUser);

        var programs = new List<DiscoveredProgram>();
        var src = new RegistryUninstallSource(FakeInstalledAppReader.With(machine, user), FakeCanonicalizer.Instance);
        // Produce two records with the same ProductCode to feed into dedup.
        var enumResult = src.Enumerate();
        // Both records share the same ProductCode → dedup should merge them.
        var merged = ProgramDedupLayer.Merge(enumResult.Programs);

        var merged1 = Assert.Single(merged);
        Assert.Equal(ProgramScope.Machine, merged1.Scope);
        Assert.Contains(ProgramSourceKind.RegistryUninstall, merged1.Sources);
    }

    // ── Test 7: Dedup — no ProductCode, same InstallPathLeaf → single merged record ──────────────

    [Fact]
    public void Dedup_same_InstallPathLeaf_no_ProductCode_merges_to_single_record()
    {
        // Two apps with no GUID key name, same install path → same leaf → dedup merges them.
        var app1 = MakeApp(
            displayName: "FooApp",
            keyName: "FooApp",
            installLocation: @"C:\Program Files\FooApp",
            source: InstalledAppSource.MachineWide64);
        var app2 = MakeApp(
            displayName: "FooApp",
            keyName: "FooApp2",
            installLocation: @"C:\Program Files\FooApp\",
            source: InstalledAppSource.MachineWide32);

        // Project both via two separate source calls (same canonicalizer, same leaf "fooapp").
        var prog1 = Project(app1);
        var prog2 = Project(app2);

        var merged = ProgramDedupLayer.Merge([prog1, prog2]);

        Assert.Single(merged);
    }

    // ── Test 8: NormalizeName variations ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Foo (64-bit)",  "foo")]
    [InlineData("Foo x64",       "foo")]
    [InlineData("Foo x64 Bar",   "foo bar")]       // Standalone arch token still stripped (mid-name).
    [InlineData("Foo 2.0",       "foo")]
    [InlineData("Müller GmbH",   "müller gmbh")]   // Non-ASCII preserved, not ASCII-folded.
    [InlineData("Linux64",       "linux64")]       // Whole-token only: substring "x64" NOT over-stripped.
    [InlineData("x64dbg",        "x64dbg")]        // "x64" glued to "dbg" → not a standalone token.
    [InlineData("max86",         "max86")]         // "x86" inside a word → preserved.
    public void NormalizeName_strips_arch_tokens_and_trailing_version(string input, string expected)
    {
        string result = ProgramJoinKeys.NormalizeName(input);
        Assert.Equal(expected, result);
    }

    // ── Test 9: TryProductCode ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{12345678-ABCD-EF01-2345-6789ABCDEF01}", true)]   // valid → lowercase GUID
    [InlineData("Notepad++",                               false)]  // not a GUID → null
    [InlineData("{12345678-ABCD-EF01-2345-6789ABCDEF}",   false)]  // wrong length → null
    [InlineData("",                                        false)]  // empty → null
    public void TryProductCode_validates_GUID_form(string keyName, bool expectNonNull)
    {
        string? result = ProgramJoinKeys.TryProductCode(keyName);
        if (expectNonNull)
        {
            Assert.NotNull(result);
            Assert.Equal(keyName.ToLowerInvariant(), result);
        }
        else
        {
            Assert.Null(result);
        }
    }

    // ── Test 10: ProgramDetector.Detect() wires up source → report ───────────────────────────────

    [Fact]
    public void Detector_returns_programs_and_one_source_report_for_single_source()
    {
        var app = MakeApp(displayName: "DetectorApp", keyName: "DetectorApp");
        var source = new RegistryUninstallSource(FakeInstalledAppReader.With(app), FakeCanonicalizer.Instance);
        var detector = new ProgramDetector([source]);

        var result = detector.Detect();

        Assert.Single(result.Programs);
        var report = Assert.Single(result.SourceReports);
        Assert.Equal(ProgramSourceKind.RegistryUninstall, report.Kind);
        Assert.Equal(ProgramSourceStatus.Ok, report.Status);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static InstalledApp MakeApp(
        string displayName,
        string keyName,
        string? installLocation = null,
        InstalledAppSource source = InstalledAppSource.MachineWide64,
        bool isSystemComponent = false)
        => new()
        {
            DisplayName       = displayName,
            RegistryKeyName   = keyName,
            Source            = source,
            InstallLocation   = installLocation,
            IsSystemComponent = isSystemComponent,
        };

    /// <summary>Project a single InstalledApp through RegistryUninstallSource (helper for dedup tests).</summary>
    private static DiscoveredProgram Project(InstalledApp app)
    {
        var reader = FakeInstalledAppReader.With(app);
        var src = new RegistryUninstallSource(reader, FakeCanonicalizer.Instance);
        return src.Enumerate().Programs[0];
    }
}

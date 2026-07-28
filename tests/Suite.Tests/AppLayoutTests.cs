using System.IO;
using WindowsCareKit.App.Deployment;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// The root-selection rule, exercised as the pure function it is: every input is injected, so these run with
/// no filesystem, no privileges and no VM. The security-critical property under test is negative — a
/// directory is never chosen because of what it contains, only because it is where this process actually is.
/// </summary>
public sealed class AppLayoutTests
{
    private const string AppDir = @"C:\Program Files\WindowsCareKit";
    private const string AppExe = @"C:\Program Files\WindowsCareKit\WindowsCareKit.exe";
    private const string LinkDir = @"C:\Users\u\AppData\Local\Microsoft\WinGet\Links";
    private const string LinkExe = @"C:\Users\u\AppData\Local\Microsoft\WinGet\Links\WindowsCareKit.exe";

    private static readonly Func<string, AppResourceKind, bool> NothingExists = (_, _) => false;
    private static readonly Func<string, AppResourceKind, bool> EverythingExists = (_, _) => true;

    private static readonly AppResource BaseTable =
        new(Path.Combine("lang", "en.json"), AppResourceKind.File);

    private static readonly AppResource ModulesFolder = new("Modules", AppResourceKind.Directory);

    private static AppLayout Create(
        string? processPath,
        string? resolvedTarget,
        string? baseDirectory,
        Func<string, AppResourceKind, bool>? exists = null)
        => AppLayout.Create(processPath, resolvedTarget, baseDirectory, exists ?? NothingExists);

    [Fact]
    public void Agreeing_identities_freeze_that_directory_as_the_root()
    {
        // AppContext.BaseDirectory always carries a trailing separator; the process path never does.
        AppLayout layout = Create(AppExe, resolvedTarget: null, baseDirectory: AppDir + Path.DirectorySeparatorChar);

        Assert.True(layout.IsDetermined);
        Assert.Equal(AppDir, layout.Root);
        Assert.Null(layout.UndeterminedReason);
    }

    [Fact]
    public void A_link_launch_is_undetermined_and_picks_neither_candidate()
    {
        // The measured winget-portable shape: both Environment.ProcessPath and AppContext.BaseDirectory report
        // the LINK's directory; only ResolveLinkTarget knows the artifact. The two therefore disagree.
        AppLayout layout = Create(LinkExe, resolvedTarget: AppExe, baseDirectory: LinkDir + Path.DirectorySeparatorChar);

        Assert.False(layout.IsDetermined);
        Assert.Throws<InvalidOperationException>(() => layout.Root);

        // Both candidates survive as diagnostics — the failure can be explained — but neither becomes the root.
        Assert.Equal(AppDir, layout.ProcessDirectory);
        Assert.Equal(LinkDir, layout.BaseDirectory);
        Assert.Contains(AppDir, layout.UndeterminedReason!);
        Assert.Contains(LinkDir, layout.UndeterminedReason!);
    }

    [Fact]
    public void Selection_never_reads_the_filesystem_so_a_resource_bearing_directory_cannot_win()
    {
        // THE security case. The exists-probe throws: if selection consulted the filesystem at all — probing
        // for a marker, an en.json, a Modules\ folder — this test would fail. A user-writable directory that
        // contains a complete copy of the app's resources must still never be selected, because "contains the
        // right files" is not evidence of identity and DirectoryModuleCatalog loads unsigned code from the root.
        Func<string, AppResourceKind, bool> explodingProbe = (_, _) =>
            throw new InvalidOperationException("root selection must not consult the filesystem");

        AppLayout layout = Create(LinkExe, resolvedTarget: AppExe, baseDirectory: LinkDir, exists: explodingProbe);

        Assert.False(layout.IsDetermined);
        Assert.Equal(AppLayoutVerificationStatus.Undetermined, layout.Verify(BaseTable).Status);
    }

    [Fact]
    public void A_fully_stocked_wrong_directory_is_still_not_the_root()
    {
        // Same case stated the other way round: pretend every probed path exists (the wrong directory holds a
        // complete, valid-looking install). The verdict is unchanged — undetermined, not "close enough".
        AppLayout layout = Create(LinkExe, resolvedTarget: AppExe, baseDirectory: LinkDir, exists: EverythingExists);

        Assert.False(layout.IsDetermined);
        Assert.Throws<InvalidOperationException>(() => layout.Resource("lang"));
    }

    [Theory]
    [InlineData(null, null, AppDir)]                 // no process identity at all
    [InlineData(AppExe, null, null)]                 // no runtime base directory
    [InlineData(@"WindowsCareKit.exe", null, AppDir)] // not fully qualified: would resolve against the ambient cwd
    [InlineData(AppExe, null, @"..\WindowsCareKit")]  // ditto on the other side
    [InlineData("   ", null, AppDir)]
    public void Unobtainable_or_ambient_inputs_are_undetermined(string? processPath, string? resolved, string? baseDir)
    {
        AppLayout layout = Create(processPath, resolved, baseDir);

        Assert.False(layout.IsDetermined);
        Assert.NotNull(layout.UndeterminedReason);
    }

    [Fact]
    public void Comparison_ignores_case_and_trailing_separators()
    {
        AppLayout layout = Create(
            @"C:\Program Files\WINDOWSCAREKIT\WindowsCareKit.exe",
            resolvedTarget: null,
            baseDirectory: AppDir + @"\");

        Assert.True(layout.IsDetermined);
    }

    [Fact]
    public void Resource_joins_under_the_root_and_refuses_to_leave_it()
    {
        AppLayout layout = Create(AppExe, null, AppDir);

        Assert.Equal(Path.Combine(AppDir, "lang", "en.json"), layout.Resource(Path.Combine("lang", "en.json")));
        Assert.Throws<ArgumentException>(() => layout.Resource(@"C:\Windows\System32"));
        Assert.Throws<ArgumentException>(() => layout.Resource(@"..\..\Users\u\evil"));
        Assert.Throws<ArgumentException>(() => layout.Resource("  "));
    }

    [Fact]
    public void Verify_distinguishes_ok_from_missing_from_undetermined()
    {
        string wanted = Path.Combine(AppDir, "lang", "en.json");
        AppLayout stocked = Create(
            AppExe, null, AppDir, exists: (p, _) => p.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        AppLayoutVerification ok = stocked.Verify(BaseTable);
        Assert.Equal(AppLayoutVerificationStatus.Ok, ok.Status);
        Assert.True(ok.IsOk);
        Assert.Empty(ok.MissingRelativePaths);

        AppLayoutVerification missing = stocked.Verify(
            BaseTable, new AppResource(Path.Combine("lang", "tr.json"), AppResourceKind.File));
        Assert.Equal(AppLayoutVerificationStatus.Missing, missing.Status);
        Assert.Equal(new[] { Path.Combine("lang", "tr.json") }, missing.MissingRelativePaths);
        Assert.False(missing.IsOk);

        AppLayoutVerification undetermined = Create(LinkExe, AppExe, LinkDir).Verify(BaseTable);
        Assert.Equal(AppLayoutVerificationStatus.Undetermined, undetermined.Status);
        Assert.Empty(undetermined.MissingRelativePaths); // NOT reported as missing: we never got to look
        Assert.NotNull(undetermined.UndeterminedReason);
    }

    [Fact]
    public void Absent_optional_inventory_is_a_valid_layout_not_a_failure()
    {
        // The supported "Base only" install type ships neither Modules\ nor manifests\. Verify is asked only
        // about what every install must have, so their absence leaves the layout Ok — while HasResource still
        // reports the truth about them for anyone who wants to say so honestly.
        string baseTable = Path.Combine(AppDir, "lang", "en.json");
        AppLayout compact = Create(
            AppExe, null, AppDir, exists: (p, _) => p.Equals(baseTable, StringComparison.OrdinalIgnoreCase));

        Assert.True(compact.Verify(BaseTable).IsOk);
        Assert.False(compact.HasResource(ModulesFolder));
        Assert.False(compact.HasResource(new AppResource("manifests", AppResourceKind.Directory)));
        Assert.True(compact.HasResource(BaseTable));
    }

    [Fact]
    public void A_resource_of_the_wrong_kind_does_not_satisfy_the_requirement()
    {
        // A regular FILE named "Modules" is not the module folder: the catalog enumerates a directory there
        // and would find nothing, so reporting it present would claim a capability the install does not have.
        // Same in the other direction for the base string table.
        string modulesPath = Path.Combine(AppDir, "Modules");
        string baseTablePath = Path.Combine(AppDir, "lang", "en.json");

        AppLayout filesOnly = Create(
            AppExe, null, AppDir, exists: (_, kind) => kind == AppResourceKind.File);
        AppLayout directoriesOnly = Create(
            AppExe, null, AppDir, exists: (_, kind) => kind == AppResourceKind.Directory);

        Assert.False(filesOnly.HasResource(ModulesFolder));      // a file sits where the folder should be
        Assert.True(filesOnly.HasResource(BaseTable));
        Assert.True(directoriesOnly.HasResource(ModulesFolder));
        Assert.False(directoriesOnly.HasResource(BaseTable));    // a directory named en.json is not the table

        // ...and the required-resource verdict agrees: the base table is required to be a file.
        Assert.Equal(AppLayoutVerificationStatus.Missing, directoriesOnly.Verify(BaseTable).Status);
        Assert.True(filesOnly.Verify(BaseTable).IsOk);

        // The probe really is asked about the right paths (guards a fake that ignores its input).
        var probed = new List<(string Path, AppResourceKind Kind)>();
        AppLayout recording = Create(AppExe, null, AppDir, exists: (p, k) =>
        {
            probed.Add((p, k));
            return false;
        });
        recording.HasResource(ModulesFolder);
        recording.Verify(BaseTable);
        Assert.Equal(
            [(modulesPath, AppResourceKind.Directory), (baseTablePath, AppResourceKind.File)],
            probed);
    }

    [Fact]
    public void Current_is_computed_once_and_frozen()
    {
        // Also a canary: if the test host ever launches in a way that makes the two identities disagree, the
        // ~20 call sites that construct I18n against the default lang directory would break here first.
        Assert.Same(AppLayout.Current, AppLayout.Current);
        Assert.True(AppLayout.Current.IsDetermined, AppLayout.Current.UndeterminedReason);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            AppLayout.Current.Root);
    }
}

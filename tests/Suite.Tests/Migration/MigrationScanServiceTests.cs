using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Execution;
using WindowsCareKit.Core.Modules.Migration.Selection;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

public sealed class MigrationScanServiceTests
{
    [Fact]
    public void Scan_uses_injected_read_only_seams_and_preserves_machine_bound_honesty()
    {
        ProfileRoots roots = MigrationTestData.Roots();
        string detect = Path.Combine(roots.AppData, "Tool");
        string settings = Path.Combine(detect, "settings.json");
        var fs = new FakeRecipeFileSystem().AddDir(detect).AddFile(settings);
        MigrationRecipe recipe = Recipe();
        var source = new FakeProgramSource();
        var service = new MigrationScanService(
            [source],
            () => roots,
            fs,
            new MachineBoundProbe(),
            () => [recipe]);

        MigrationScanResult result = service.Scan();

        Assert.Equal(1, source.CallCount);
        Assert.Equal(roots.UserProfile, result.ProfileRoot);
        Assert.Single(result.Detection.Programs);
        MigrationSelectionCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal(settings, candidate.SourcePath);
        Assert.Equal(MigrationSourceKind.File, candidate.SourceKind);
        Assert.True(candidate.Meta.HasMachineBoundContent);
        MigrationBadgePresentation badge = MigrationBadgePresenter.Derive(
            candidate.Meta, candidate.RestoreTier, candidate.IsRegenerable);
        Assert.False(badge.MayClaimWorks);
        Assert.NotEqual("✅", badge.Glyph);
    }

    [Fact]
    public void Exists_false_recipe_without_detection_presence_produces_no_phantom_row()
    {
        ProfileRoots roots = MigrationTestData.Roots();
        MigrationRecipe recipe = AlwaysMatchRecipe("putty.putty", "PuTTY", "PuTTY.PuTTY");
        var service = new MigrationScanService(
            [new FakeProgramSource([])],
            () => roots,
            new FakeRecipeFileSystem(),
            new CleanProbe(),
            () => [recipe]);

        MigrationScanResult result = service.Scan();

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Exists_false_recipe_is_allowed_when_matching_program_is_detected()
    {
        ProfileRoots roots = MigrationTestData.Roots();
        MigrationRecipe recipe = AlwaysMatchRecipe("putty.putty", "PuTTY", "PuTTY.PuTTY");
        DiscoveredProgram putty = Program("PuTTY") with { ReinstallId = "PuTTY.PuTTY" };
        var service = new MigrationScanService(
            [new FakeProgramSource([putty])],
            () => roots,
            new FakeRecipeFileSystem(),
            new CleanProbe(),
            () => [recipe]);

        MigrationScanResult result = service.Scan();

        MigrationSelectionCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal("putty.putty", candidate.Meta.RecipeId);
        Assert.True(candidate.IsRecognized);
    }

    [Fact]
    public void Has_cloud_backup_comes_from_injected_onedrive_containment_signal()
    {
        ProfileRoots roots = MigrationTestData.Roots();
        string source = Path.Combine(roots.UserProfile, "OneDrive", "Documents", "settings.json");
        var fs = new FakeRecipeFileSystem().AddDir(Path.GetDirectoryName(source)!).AddFile(source);
        MigrationRecipe recipe = new(
            SchemaVersion: 3,
            Id: "cloud.tool",
            DisplayName: "Cloud Tool",
            Category: "projects",
            Detect: new RecipeDetect(KnownFolder.UserProfile, "OneDrive/Documents/settings.json", true),
            Items: [new RecipeItem("OneDrive/Documents/settings.json", [], [])],
            Exclude: [],
            SecretRule: "global",
            PortabilityClass: PortabilityClass.ProfileRelative,
            Restore: new RecipeRestore(RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, []))
        {
            RestoreTier = RestoreTier.ConfigCopy,
        };
        var service = new MigrationScanService(
            [new FakeProgramSource([])],
            () => roots,
            fs,
            new CleanProbe(),
            () => [recipe],
            new OneDriveKnownFolderContainmentSignal([Path.Combine(roots.UserProfile, "OneDrive")]));

        MigrationSelectionCandidate candidate = Assert.Single(service.Scan().Candidates);

        Assert.True(candidate.HasCloudBackup);
    }

    [Fact]
    public void OneDrive_containment_signal_is_false_for_unknown_or_outside_paths()
    {
        var signal = new OneDriveKnownFolderContainmentSignal([@"C:\Users\alice\OneDrive"]);

        Assert.False(signal.HasCloudBackup(null));
        Assert.False(signal.HasCloudBackup(@"C:\Users\alice\Documents\settings.json"));
        Assert.True(signal.HasCloudBackup(@"C:\Users\alice\OneDrive\Documents\settings.json"));
    }

    private static MigrationRecipe Recipe()
        => new(
            SchemaVersion: 3,
            Id: "test.tool",
            DisplayName: "Test Tool",
            Category: "dev-tools",
            Detect: new RecipeDetect(KnownFolder.AppData, "Tool", true),
            Items: [new RecipeItem("Tool/settings.json", [], [])],
            Exclude: [],
            SecretRule: "global",
            PortabilityClass: PortabilityClass.ProfileRelative,
            Restore: new RecipeRestore(
                RestoreStrategy.ConfigWrite,
                RestorePhase.ConfigWrite,
                []))
        {
            RestoreTier = RestoreTier.ConfigCopy,
            MigrationMeta = new MigrationRecipeMeta(
                new LocalizedText("Settings are copied.", "Ayarlar kopyalanır."),
                [],
                ["Sign in again."],
                InstallerSource.Unknown,
                LicenseSource.AccountLogin,
                RequiresRelogin: true,
                BackedUpButNotRestored: false,
                SurvivesOnOtherDrive: false),
        };

    private static MigrationRecipe AlwaysMatchRecipe(string id, string displayName, string wingetId)
        => new(
            SchemaVersion: 3,
            Id: id,
            DisplayName: displayName,
            Category: "dev-tools",
            Detect: new RecipeDetect(KnownFolder.UserProfile, ".", false),
            Items:
            [
                new RecipeItem("export-note", [], [])
                {
                    Kind = RecipeItemKind.ManualTodo,
                    ManualTodo = ["Export manually."],
                },
            ],
            Exclude: [],
            SecretRule: "global",
            PortabilityClass: PortabilityClass.Partial,
            Restore: new RecipeRestore(
                RestoreStrategy.ConfigWrite,
                RestorePhase.ConfigWrite,
                []))
        {
            RestoreTier = RestoreTier.InventoryOnly,
            WingetId = wingetId,
            InstallPathHint = [displayName],
            Install = new RecipeInstall(RecipeInstallMethod.Winget, wingetId, null, null, false, false),
        };

    private static DiscoveredProgram Program(string displayName)
        => new()
        {
            Id = displayName.ToLowerInvariant(),
            DisplayName = displayName,
            NormalizedName = ProgramJoinKeys.NormalizeName(displayName),
            Scope = ProgramScope.CurrentUser,
            Sources = [ProgramSourceKind.RegistryUninstall],
        };

    private sealed class FakeProgramSource : IProgramSource
    {
        private readonly IReadOnlyList<DiscoveredProgram> _programs;

        public FakeProgramSource()
            : this([Program("Test Tool")])
        {
        }

        public FakeProgramSource(IReadOnlyList<DiscoveredProgram> programs)
            => _programs = programs;

        public int CallCount { get; private set; }
        public ProgramSourceKind Kind => ProgramSourceKind.RegistryUninstall;

        public ProgramEnumeration Enumerate()
        {
            CallCount++;
            return new ProgramEnumeration(
                _programs,
                new ProgramSourceReport(Kind, ProgramSourceStatus.Ok, _programs.Count));
        }
    }

    private sealed class MachineBoundProbe : IContentSignatureProbe
    {
        public ContentSignature ProbeFile(string path, ContentSignatureOptions? options = null)
            => new() { HasDpapiBlob = true, BytesInspected = 32 };
    }

    private sealed class CleanProbe : IContentSignatureProbe
    {
        public ContentSignature ProbeFile(string path, ContentSignatureOptions? options = null)
            => new() { BytesInspected = 32 };
    }
}

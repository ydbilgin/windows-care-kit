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

    private sealed class FakeProgramSource : IProgramSource
    {
        public int CallCount { get; private set; }
        public ProgramSourceKind Kind => ProgramSourceKind.RegistryUninstall;

        public ProgramEnumeration Enumerate()
        {
            CallCount++;
            DiscoveredProgram program = new()
            {
                Id = "test-tool",
                DisplayName = "Test Tool",
                NormalizedName = "test tool",
                Scope = ProgramScope.CurrentUser,
                Sources = [Kind],
            };
            return new ProgramEnumeration(
                [program],
                new ProgramSourceReport(Kind, ProgramSourceStatus.Ok, 1));
        }
    }

    private sealed class MachineBoundProbe : IContentSignatureProbe
    {
        public ContentSignature ProbeFile(string path)
            => new() { HasDpapiBlob = true, BytesInspected = 32 };
    }
}

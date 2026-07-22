using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Selection;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

public class SmartDefaultScorerCloudBackupTests
{
    [Fact]
    public void Unknown_cloud_backup_is_scored_as_protectively_as_not_backed_up()
    {
        MigrationSelectionCandidate unknown = Scorable() with { CloudBackup = CloudBackupStatus.Unknown };
        MigrationSelectionCandidate notBacked = Scorable() with { CloudBackup = CloudBackupStatus.NotBackedUp };
        MigrationSelectionCandidate backed = Scorable() with { CloudBackup = CloudBackupStatus.BackedUp };
        MigrationBadgePresentation badge = MigrationBadgePresenter.Derive(
            unknown.Meta, unknown.RestoreTier, unknown.IsRegenerable);

        int unknownScore = SmartDefaultScorer.Score(unknown, badge).IrreplaceabilityScore;
        int notBackedScore = SmartDefaultScorer.Score(notBacked, badge).IrreplaceabilityScore;
        int backedScore = SmartDefaultScorer.Score(backed, badge).IrreplaceabilityScore;

        Assert.Equal(notBackedScore, unknownScore);   // Unknown must not lower protection
        Assert.Equal(unknownScore - 1, backedScore);  // only a VERIFIED backup removes the factor
    }

    private static MigrationItemMeta Meta()
        => new("recipe", "entry", PortabilityClass.ProfileRelative, RestoreStrategy.ConfigWrite,
            RestorePhase.ConfigWrite, System.Array.Empty<string>());

    private static MigrationSelectionCandidate Scorable()
        => new()
        {
            Id = "scorable",
            DisplayName = "Scorable",
            RecipeCategory = "dev-tools",
            Meta = Meta(),
            RestoreTier = RestoreTier.ConfigCopy,
            SourceKind = MigrationSourceKind.None,
            IsOnSystemDrive = true,
            IsUnique = true,
            IsRegenerable = false,
            IsRecognized = true,
            IsAutoStub = false,
            HasInstallRecord = true,
        };
}

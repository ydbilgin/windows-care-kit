using WindowsCareKit.Core.Modules.Migration.Detection;

namespace WindowsCareKit.Core.Modules.Migration.Selection;

/// <summary>Integer counts only: no percentage and no merged "handled" score.</summary>
public sealed record CoverageRatio(int Available, int Total)
{
    public override string ToString() => $"{Available}/{Total}";
}

public sealed record CategoryCoverage(
    MigrationCategory Category,
    CoverageRatio AppReinstallAvailable,
    CoverageRatio ConfigRestoreAvailable,
    CoverageRatio DetectionCoverage);

public sealed record FeasibilityCeilingText(
    string Tr,
    string En,
    string TransportTr,
    string TransportEn,
    CoverageRatio DetectionCoverage);

public static class MigrationCoverageCalculator
{
    public static IReadOnlyList<CategoryCoverage> ByCategory(
        IReadOnlyList<MigrationSelectionGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return groups.Select(group =>
        {
            int total = group.Items.Count;
            int appReinstall = group.Items.Count(item =>
                item.Candidate.InstallMethod is RecipeInstallMethod.Winget or RecipeInstallMethod.Npm);
            int configRestore = group.Items.Count(item =>
                item.Candidate.RestoreTier >= RestoreTier.ConfigCopy);
            int detectedWithRecord = group.Items.Count(item => item.Candidate.HasInstallRecord);
            return new CategoryCoverage(
                group.Category,
                new CoverageRatio(appReinstall, total),
                new CoverageRatio(configRestore, total),
                new CoverageRatio(detectedWithRecord, total));
        }).ToArray();
    }

    /// <summary>B-5 global recall oracle: launchable-without-install-record rows remain in the denominator.</summary>
    public static CoverageRatio DetectionCoverage(DetectionResult detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        int total = detection.Programs.Count;
        int uncovered = Math.Clamp(detection.LaunchableWithoutInstallRecordCount, 0, total);
        return new CoverageRatio(total - uncovered, total);
    }

    public static FeasibilityCeilingText BuildBanner(DetectionResult detection)
    {
        CoverageRatio coverage = DetectionCoverage(detection);
        return new FeasibilityCeilingText(
            Tr: $"WCK tam makine klonu değildir: ayarları/save'leri ve yeniden kurulum-giriş planını taşır; uygulama binary'lerini veya eski PC'ye kilitli parola/tokenları taşımaz. Tespit kapsamı: {coverage}. “Yedeklendi” hiçbir zaman “geri yüklendi ve çalışıyor” demek değildir.",
            En: $"WCK is not a full-machine clone: it carries config/saves and a reinstall/re-login plan; it does not copy app binaries or passwords/tokens locked to the old PC. Detection coverage: {coverage}. “Backed up” never means “restored and working.”",
            TransportTr: "Paket seçtiğin konuma yazılır; yeni PC'ye taşımak SENİN adımın. LAN/canlı-transfer yok.",
            TransportEn: "The package is written where you choose; moving it to the new PC is YOUR step. No LAN/live transfer.",
            DetectionCoverage: coverage);
    }
}

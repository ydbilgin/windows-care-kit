using WindowsCareKit.Core.Modules.Migration.Detection;

namespace WindowsCareKit.Core.Modules.Migration.Selection;

/// <summary>Immutable scan/profile confirmation state. Source failures are surfaced but do not hide the grid.</summary>
public sealed record ScanReadyGate
{
    private ScanReadyGate(
        bool enumerationComplete,
        DetectionResult? detection,
        string resolvedProfileRoot,
        bool profileConfirmed)
    {
        EnumerationComplete = enumerationComplete;
        Detection = detection;
        ResolvedProfileRoot = resolvedProfileRoot;
        ProfileConfirmed = profileConfirmed;
    }

    public bool EnumerationComplete { get; }
    public DetectionResult? Detection { get; }
    public string ResolvedProfileRoot { get; }
    public bool ProfileConfirmed { get; private init; }
    public bool CanSelect => EnumerationComplete && ProfileConfirmed;
    public int ProgramCount => Detection?.Programs.Count ?? 0;
    public int SourceCount => Detection?.SourceReports.Count ?? 0;
    public IReadOnlyList<ProgramSourceReport> SourceReports
        => Detection?.SourceReports ?? Array.Empty<ProgramSourceReport>();

    public string ProfileName
    {
        get
        {
            string trimmed = Path.TrimEndingDirectorySeparator(ResolvedProfileRoot);
            return Path.GetFileName(trimmed);
        }
    }

    public static ScanReadyGate Pending(string resolvedProfileRoot)
        => new(false, null, ValidateRoot(resolvedProfileRoot), false);

    public static ScanReadyGate Complete(DetectionResult detection, string resolvedProfileRoot)
    {
        ArgumentNullException.ThrowIfNull(detection);
        return new(true, detection, ValidateRoot(resolvedProfileRoot), false);
    }

    public ScanReadyGate ConfirmProfile()
        => this with { ProfileConfirmed = true };

    public string ConfirmationTr
        => $"Taranan kullanıcı: {ProfileName} ({ResolvedProfileRoot}). Farklı kullanıcıyı taşıyorsan WCK'yi o kullanıcı olarak çalıştır.";

    public string ConfirmationEn
        => $"Scanning user {ProfileName} ({ResolvedProfileRoot}). Migrating a different user? Run WCK as that user.";

    private static string ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("resolved profile root is required", nameof(root));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
}

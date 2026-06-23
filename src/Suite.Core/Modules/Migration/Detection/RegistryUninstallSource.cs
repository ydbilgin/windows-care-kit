using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Migration.Detection;

/// <summary>
/// <see cref="IProgramSource"/> that wraps <see cref="IInstalledAppReader"/> to project classic
/// Win32 / MSI registry entries into normalized <see cref="DiscoveredProgram"/> records.
///
/// This class is a pure wrapper — it does NOT modify <see cref="IInstalledAppReader"/> or any
/// existing tested code. It is net-new.
/// </summary>
public sealed class RegistryUninstallSource : IProgramSource
{
    private readonly IInstalledAppReader _reader;
    private readonly IPathCanonicalizer _canon;

    public RegistryUninstallSource(IInstalledAppReader reader, IPathCanonicalizer canon)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(canon);
        _reader = reader;
        _canon = canon;
    }

    public ProgramSourceKind Kind => ProgramSourceKind.RegistryUninstall;

    public ProgramEnumeration Enumerate()
    {
        IReadOnlyList<InstalledApp> apps;
        try
        {
            apps = _reader.ReadAll();
        }
        catch
        {
            // Any exception from the reader → SourceFailed; never propagate.
            return Fail();
        }

        // B.3 non-vacuous rule: the real uninstall hive is never empty on a live machine.
        // An empty result therefore indicates a probe failure, not "zero apps installed".
        if (apps.Count == 0)
            return Fail();

        var programs = new List<DiscoveredProgram>(apps.Count);
        foreach (var app in apps)
        {
            string? productCode = ProgramJoinKeys.TryProductCode(app.RegistryKeyName);
            string? leaf = ProgramJoinKeys.InstallPathLeaf(app.InstallLocation, _canon);
            string normalizedName = ProgramJoinKeys.NormalizeName(app.DisplayName);
            string publisher = app.Publisher ?? string.Empty;

            string id = productCode
                ?? leaf
                ?? $"{normalizedName}|{publisher.ToLowerInvariant()}";

            ProgramScope scope = app.Source switch
            {
                InstalledAppSource.MachineWide64 => ProgramScope.Machine,
                InstalledAppSource.MachineWide32 => ProgramScope.Machine,
                InstalledAppSource.CurrentUser   => ProgramScope.CurrentUser,
                _                                => ProgramScope.Machine,
            };

            programs.Add(new DiscoveredProgram
            {
                Id              = id,
                DisplayName     = app.DisplayName,
                Publisher       = app.Publisher,
                Version         = app.DisplayVersion,
                InstallLocation = app.InstallLocation,
                InstallPathLeaf = leaf,
                ProductCode     = productCode,
                NormalizedName  = normalizedName,
                Scope           = scope,
                Sources         = [ProgramSourceKind.RegistryUninstall],
                IsSystemComponent = app.IsSystemComponent,
                ReinstallId     = null,
                PackageFamilyName = null,
            });
        }

        return new ProgramEnumeration(
            programs,
            new ProgramSourceReport(ProgramSourceKind.RegistryUninstall, ProgramSourceStatus.Ok, programs.Count));
    }

    private static ProgramEnumeration Fail() =>
        new(
            [],
            new ProgramSourceReport(ProgramSourceKind.RegistryUninstall, ProgramSourceStatus.SourceFailed, 0));
}

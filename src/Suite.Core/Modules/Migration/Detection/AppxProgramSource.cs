using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Migration.Detection;

/// <summary><see cref="IProgramSource"/> wrapping current-user AppX/MSIX package inventory.</summary>
public sealed class AppxProgramSource : IProgramSource
{
    private readonly IAppxReader _reader;
    private readonly IPathCanonicalizer _canon;

    public AppxProgramSource(IAppxReader reader, IPathCanonicalizer canon)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _canon = canon ?? throw new ArgumentNullException(nameof(canon));
    }

    public ProgramSourceKind Kind => ProgramSourceKind.Appx;

    public ProgramEnumeration Enumerate()
    {
        IReadOnlyList<InstalledAppx> packages;
        try
        {
            packages = _reader.ReadCurrentUserPackages();
        }
        catch
        {
            return new ProgramEnumeration([], new ProgramSourceReport(ProgramSourceKind.Appx, ProgramSourceStatus.SourceFailed, 0));
        }

        if (packages.Count == 0)
            return new ProgramEnumeration([], new ProgramSourceReport(ProgramSourceKind.Appx, ProgramSourceStatus.SourceUnavailable, 0));

        var programs = new List<DiscoveredProgram>(packages.Count);
        foreach (InstalledAppx package in packages)
        {
            if (string.IsNullOrWhiteSpace(package.DisplayName))
                continue;

            string? pfn = ProgramJoinKeys.PackageFamilyName(package.PackageFamilyName)
                ?? ProgramJoinKeys.PackageFamilyNameFromFullName(package.PackageFullName);
            string? leaf = ProgramJoinKeys.InstallPathLeaf(package.InstallLocation, _canon);
            string normalizedName = ProgramJoinKeys.NormalizeName(package.DisplayName);
            string id = pfn ?? leaf ?? $"{normalizedName}|{(package.PublisherDisplayName ?? string.Empty).ToLowerInvariant()}";

            programs.Add(new DiscoveredProgram
            {
                Id = id,
                DisplayName = package.DisplayName.Trim(),
                Publisher = NullIfWhiteSpace(package.PublisherDisplayName),
                Version = NullIfWhiteSpace(package.Version),
                InstallLocation = NullIfWhiteSpace(package.InstallLocation),
                InstallPathLeaf = leaf,
                ProductCode = null,
                NormalizedName = normalizedName,
                Scope = ProgramScope.CurrentUser,
                Sources = [ProgramSourceKind.Appx],
                IsSystemComponent = package.IsFrameworkOrSystem,
                ReinstallId = null,
                PackageFamilyName = pfn,
            });
        }

        if (programs.Count == 0)
            return new ProgramEnumeration([], new ProgramSourceReport(ProgramSourceKind.Appx, ProgramSourceStatus.SourceFailed, 0));

        return new ProgramEnumeration(programs, new ProgramSourceReport(ProgramSourceKind.Appx, ProgramSourceStatus.Ok, programs.Count));
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

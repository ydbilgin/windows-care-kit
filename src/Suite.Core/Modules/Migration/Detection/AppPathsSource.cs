using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Migration.Detection;

/// <summary>Read-only App Paths registry inventory.</summary>
public sealed class AppPathsSource : IProgramSource
{
    private const string AppPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private readonly IRegistryProbe _registry;
    private readonly IPathCanonicalizer _canon;

    public AppPathsSource(IRegistryProbe registry, IPathCanonicalizer canon)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _canon = canon ?? throw new ArgumentNullException(nameof(canon));
    }

    public ProgramSourceKind Kind => ProgramSourceKind.AppPaths;

    public ProgramEnumeration Enumerate()
    {
        try
        {
            var programs = new List<DiscoveredProgram>();
            ReadFrom(RegistryHive.LocalMachine, RegistryView.Registry64, ProgramScope.Machine, programs);
            ReadFrom(RegistryHive.LocalMachine, RegistryView.Registry32, ProgramScope.Machine, programs);
            ReadFrom(RegistryHive.CurrentUser, RegistryView.Registry64, ProgramScope.CurrentUser, programs);
            ReadFrom(RegistryHive.CurrentUser, RegistryView.Registry32, ProgramScope.CurrentUser, programs);

            if (programs.Count == 0)
                return Fail();
            return new ProgramEnumeration(
                programs,
                new ProgramSourceReport(ProgramSourceKind.AppPaths, ProgramSourceStatus.Ok, programs.Count));
        }
        catch
        {
            return Fail();
        }
    }

    private void ReadFrom(RegistryHive hive, RegistryView view, ProgramScope scope, List<DiscoveredProgram> sink)
    {
        foreach (string subName in _registry.GetSubKeyNames(hive, view, AppPathsRoot))
        {
            RegistryKeySnapshot? key = _registry.ReadKey(hive, view, $@"{AppPathsRoot}\{subName}");
            if (key is null)
                continue;

            string? target = key.GetString(string.Empty);
            string? installLocation = InstallLocationOf(target) ?? key.GetString("Path");
            string displayName = Path.GetFileNameWithoutExtension(subName);
            if (string.IsNullOrWhiteSpace(displayName))
                continue;

            string? leaf = ProgramJoinKeys.InstallPathLeaf(installLocation, _canon);
            string normalizedName = ProgramJoinKeys.NormalizeName(displayName);
            string id = leaf ?? $"{normalizedName}|";

            sink.Add(new DiscoveredProgram
            {
                Id = id,
                DisplayName = displayName,
                Publisher = null,
                Version = null,
                InstallLocation = installLocation,
                InstallPathLeaf = leaf,
                ProductCode = null,
                NormalizedName = normalizedName,
                Scope = scope,
                Sources = [ProgramSourceKind.AppPaths],
                IsSystemComponent = false,
                ReinstallId = null,
                PackageFamilyName = null,
            });
        }
    }

    private static string? InstallLocationOf(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return null;
        try
        {
            string trimmed = target.Trim().Trim('"');
            return Path.GetDirectoryName(trimmed);
        }
        catch
        {
            return null;
        }
    }

    private static ProgramEnumeration Fail() =>
        new([], new ProgramSourceReport(ProgramSourceKind.AppPaths, ProgramSourceStatus.SourceFailed, 0));
}

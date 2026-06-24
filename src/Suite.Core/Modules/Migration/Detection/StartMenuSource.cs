using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Migration.Detection;

public sealed record StartMenuShortcut(string DisplayName, string LinkPath, string? TargetPath);

/// <summary>Read-only shortcut reader. Production uses IShellLinkW; tests use an in-memory fake.</summary>
public interface IStartMenuShortcutReader
{
    IReadOnlyList<StartMenuShortcut> ReadShortcuts();
}

/// <summary>Start Menu .lnk inventory projected into low-confidence launchable program records.</summary>
public sealed class StartMenuSource : IProgramSource
{
    private readonly IStartMenuShortcutReader _reader;
    private readonly IPathCanonicalizer _canon;

    public StartMenuSource(IStartMenuShortcutReader reader, IPathCanonicalizer canon)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _canon = canon ?? throw new ArgumentNullException(nameof(canon));
    }

    public ProgramSourceKind Kind => ProgramSourceKind.StartMenu;

    public ProgramEnumeration Enumerate()
    {
        IReadOnlyList<StartMenuShortcut> shortcuts;
        try
        {
            shortcuts = _reader.ReadShortcuts();
        }
        catch
        {
            return Fail();
        }

        if (shortcuts.Count == 0)
            return Fail();

        var programs = new List<DiscoveredProgram>();
        foreach (StartMenuShortcut shortcut in shortcuts)
        {
            string? target = shortcut.TargetPath;
            if (string.IsNullOrWhiteSpace(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            string? installLocation = Path.GetDirectoryName(target.Trim('"'));
            string? leaf = ProgramJoinKeys.InstallPathLeaf(installLocation, _canon);
            string displayName = string.IsNullOrWhiteSpace(shortcut.DisplayName)
                ? Path.GetFileNameWithoutExtension(shortcut.LinkPath)
                : shortcut.DisplayName.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
                continue;

            string normalizedName = ProgramJoinKeys.NormalizeName(displayName);
            programs.Add(new DiscoveredProgram
            {
                Id = leaf ?? $"{normalizedName}|",
                DisplayName = displayName,
                Publisher = null,
                Version = null,
                InstallLocation = installLocation,
                InstallPathLeaf = leaf,
                ProductCode = null,
                NormalizedName = normalizedName,
                Scope = IsAllUsers(shortcut.LinkPath) ? ProgramScope.Machine : ProgramScope.CurrentUser,
                Sources = [ProgramSourceKind.StartMenu],
                IsSystemComponent = false,
                ReinstallId = null,
                PackageFamilyName = null,
            });
        }

        if (programs.Count == 0)
            return Fail();

        return new ProgramEnumeration(programs, new ProgramSourceReport(ProgramSourceKind.StartMenu, ProgramSourceStatus.Ok, programs.Count));
    }

    private static bool IsAllUsers(string linkPath)
        => !string.IsNullOrWhiteSpace(linkPath)
           && linkPath.Contains(@"\ProgramData\", StringComparison.OrdinalIgnoreCase);

    private static ProgramEnumeration Fail() =>
        new([], new ProgramSourceReport(ProgramSourceKind.StartMenu, ProgramSourceStatus.SourceFailed, 0));
}

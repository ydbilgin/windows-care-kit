using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Tests;

/// <summary>An in-memory <see cref="IPathCanonicalizer"/> so SafetyGate path policy can be tested
/// deterministically. Unmapped paths resolve to themselves (identity).</summary>
internal sealed class FakeCanonicalizer : IPathCanonicalizer
{
    private readonly Dictionary<string, CanonicalPath> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _longPathMap = new(StringComparer.OrdinalIgnoreCase);

    public FakeCanonicalizer Map(string original, string final, bool reparse = false, bool resolved = true)
    {
        _map[original] = new CanonicalPath(original, final, reparse, resolved);
        return this;
    }

    /// <summary>Map a short/8.3-looking literal to the long path it expands to (L12 literal-branch tests).</summary>
    public FakeCanonicalizer MapLongPath(string original, string expanded)
    {
        _longPathMap[original] = expanded;
        return this;
    }

    public CanonicalPath Canonicalize(string path)
        => _map.TryGetValue(path, out var c) ? c : new CanonicalPath(path, path, false, true);

    /// <summary>Behavior-preserving default = <see cref="System.IO.Path.GetFullPath(string)"/>; mapped inputs expand.</summary>
    public string ExpandLongPath(string path)
        => _longPathMap.TryGetValue(path, out var expanded) ? expanded : System.IO.Path.GetFullPath(path);
}

internal sealed class FakeCurrentSidProvider(string? sid = TestData.CurrentUserSid) : ICurrentSidProvider
{
    public string? GetCurrentSid() => sid;
}

/// <summary>Shared deterministic policy + action factories for the tests.</summary>
internal static class TestData
{
    public const string CurrentUserSid = "S-1-5-21-1234567890-1001";
    public const string OtherUserSid = "S-1-5-21-1234567890-2002";

    public static ProtectedResources Policy() => new(
        protectedDirectories: new[]
        {
            @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)",
            @"C:\ProgramData", @"C:\Users", @"C:\Users\alice",
        },
        windowsDirectory: @"C:\Windows",
        protectedProcessNames: ProtectedResources.DefaultProtectedProcessNames,
        criticalServiceNames: ProtectedResources.DefaultCriticalServiceNames,
        protectedRegistryKeys: ProtectedResources.DefaultProtectedRegistryKeys,
        wholeSubtreeRegistryRoots: ProtectedResources.DefaultWholeSubtreeRegistryRoots,
        commandDenyList: ProtectedResources.DefaultCommandDenyList,
        writeProtectedRoots: new[] { @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData" },
        usersRoot: @"C:\Users",
        currentUserProfile: @"C:\Users\alice");

    public static SafetyGate Gate(IPathCanonicalizer? canon = null, string? currentSid = CurrentUserSid)
        => new(Policy(), canon ?? new FakeCanonicalizer(), new FakeCurrentSidProvider(currentSid));

    public static FileDeleteAction FileDelete(string path, bool recycle = true)
        => new() { Path = path, ToRecycleBin = recycle, Description = "delete " + path, Reason = "test" };

    public static RegistryDeleteAction RegKey(RegistryHive hive, string sub, RegistryView view = RegistryView.Registry64)
        => new() { Hive = hive, SubKeyPath = sub, View = view, Description = "del key " + sub, Reason = "test" };

    public static RegistryDeleteAction RegValue(RegistryHive hive, string sub, string value)
        => new() { Hive = hive, SubKeyPath = sub, ValueName = value, Description = "del value", Reason = "test" };

    public static ServiceDeleteAction Service(string name, ServiceOperation op = ServiceOperation.Delete)
        => new() { ServiceName = name, Operation = op, Description = "svc " + name, Reason = "test" };

    public static TaskDeleteAction Task(string path, TaskOperation op = TaskOperation.Delete)
        => new() { TaskPath = path, Operation = op, Description = "task " + path, Reason = "test" };

    public static CommandAction Command(string file, params string[] args)
        => new() { FileName = file, Arguments = args, Description = "run " + file, Reason = "test" };

    public static CopyAction Copy(string src, string dst)
        => new() { Source = src, Destination = dst, Description = "copy", Reason = "test" };

    public static RestoreMergeAction Restore(string src, string dst)
        => new() { Source = src, Destination = dst, Description = "restore", Reason = "test" };

    public static InstalledApp App(
        string displayName = "SomeApp",
        InstalledAppSource source = InstalledAppSource.MachineWide64,
        string? uninstall = null,
        string? quietUninstall = null,
        string? publisher = "SomeVendor",
        string? installLocation = null,
        string regKeyName = "SomeApp")
        => new()
        {
            DisplayName = displayName,
            Publisher = publisher,
            UninstallString = uninstall,
            QuietUninstallString = quietUninstall,
            InstallLocation = installLocation,
            RegistryKeyName = regKeyName,
            Source = source,
        };
}

/// <summary>A configurable <see cref="ILeftoverProbe"/> for scanner tests.</summary>
internal sealed class FakeLeftoverProbe : ILeftoverProbe
{
    public List<LeftoverDirectory> Directories { get; } = new();
    public List<LeftoverRegistryKey> RegistryKeys { get; } = new();
    public List<LeftoverService> Services { get; } = new();
    public List<LeftoverTask> Tasks { get; } = new();

    public IReadOnlyList<LeftoverDirectory> FindLeftoverDirectories(InstalledApp app) => Directories;
    public IReadOnlyList<LeftoverRegistryKey> FindLeftoverRegistryKeys(InstalledApp app) => RegistryKeys;
    public IReadOnlyList<LeftoverService> FindRelatedServices(InstalledApp app) => Services;
    public IReadOnlyList<LeftoverTask> FindRelatedTasks(InstalledApp app) => Tasks;
}

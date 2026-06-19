namespace WindowsCareKit.Core.Safety;

/// <summary>
/// The policy data the <see cref="SafetyGate"/> enforces: which directories, registry keys,
/// processes, services, scheduled tasks and command interpreters are off-limits. Construct it from
/// the live machine with <see cref="ForCurrentSystem"/>, or hand it explicit values in tests so the
/// path/registry logic can be verified deterministically regardless of the test box (spec §3).
/// </summary>
public sealed class ProtectedResources
{
    /// <summary>Exact directories that must never be deleted (normalized, lowercased, no trailing slash).</summary>
    public IReadOnlyList<string> ProtectedDirectories { get; }

    /// <summary>The Windows directory; it and everything under it is protected.</summary>
    public string WindowsDirectory { get; }

    /// <summary>Process base names (lowercased, no <c>.exe</c>) that force-kill must never touch.</summary>
    public IReadOnlySet<string> ProtectedProcessNames { get; }

    /// <summary>Service names (lowercased) that must never be stopped/disabled/deleted.</summary>
    public IReadOnlySet<string> CriticalServiceNames { get; }

    /// <summary>
    /// Security / update / search services (lowercased) that are not "critical-to-boot" but whose
    /// <c>Disable</c> or <c>Delete</c> is refused: turning them off persistently weakens the machine
    /// (Defender, Windows Update, BITS, Search). A transient <c>Stop</c> is still permitted (spec §1.1, L1).
    /// </summary>
    public IReadOnlySet<string> ProtectedServiceNames { get; }

    /// <summary>Registry subkeys (lowercased, hive-agnostic) whose key-delete is refused.</summary>
    public IReadOnlyList<string> ProtectedRegistryKeys { get; }

    /// <summary>First-segment registry roots whose whole subtree is protected (system/security/sam…).</summary>
    public IReadOnlySet<string> WholeSubtreeRegistryRoots { get; }

    /// <summary>
    /// Keys where deleting a single *value* is legitimately allowed even though the key sits under a
    /// protected subtree — e.g. the Run/RunOnce startup keys (the startup manager removes one value).
    /// Without this allowlist the value-delete rule would block removing a startup entry.
    /// </summary>
    public IReadOnlySet<string> ValueDeleteAllowedKeys { get; }

    /// <summary>
    /// Narrow set of high-value keys whose *values* must never be deleted (Winlogon Userinit/Shell,
    /// Image File Execution Options, AppInit, policies). A value-delete at or under one of these is
    /// refused unless the key is on <see cref="ValueDeleteAllowedKeys"/>.
    /// </summary>
    public IReadOnlyList<string> ProtectedValueKeys { get; }

    /// <summary>Command interpreters that may never be launched (no shell-string execution).</summary>
    public IReadOnlySet<string> CommandDenyList { get; }

    /// <summary>
    /// Interpreter / LOLBin executable STEMS (no extension, lowercased) that may never be launched as a
    /// <c>CommandAction</c>, regardless of extension/path. Matched against the normalized stem so
    /// <c>cmd</c>, <c>cmd.com</c>, <c>cmd.exe.</c> and <c>C:\…\cmd.exe</c> all resolve to <c>cmd</c> (spec §7).
    /// </summary>
    public IReadOnlySet<string> CommandDenyStems { get; }

    /// <summary>
    /// Roots under which WRITING a file is refused (copy/restore destinations): the Windows tree,
    /// Program Files (both), and ProgramData. Stricter than the delete policy — uninstall may delete
    /// an app folder under Program Files, but nothing may CREATE files there (DLL-plant / all-users
    /// startup persistence defense, spec §3).
    /// </summary>
    public IReadOnlyList<string> WriteProtectedRoots { get; }

    /// <summary>The Users root (e.g. C:\Users). Writing under it is allowed only inside the current profile.</summary>
    public string UsersRoot { get; }

    /// <summary>The current user's profile; the only sub-tree of <see cref="UsersRoot"/> writable as a target.</summary>
    public string CurrentUserProfile { get; }

    public ProtectedResources(
        IEnumerable<string> protectedDirectories,
        string windowsDirectory,
        IEnumerable<string> protectedProcessNames,
        IEnumerable<string> criticalServiceNames,
        IEnumerable<string> protectedRegistryKeys,
        IEnumerable<string> wholeSubtreeRegistryRoots,
        IEnumerable<string> commandDenyList,
        IEnumerable<string>? valueDeleteAllowedKeys = null,
        IEnumerable<string>? protectedValueKeys = null,
        IEnumerable<string>? writeProtectedRoots = null,
        string? usersRoot = null,
        string? currentUserProfile = null,
        IEnumerable<string>? protectedServiceNames = null)
    {
        ProtectedDirectories = protectedDirectories.Select(NormalizeDirectory).Where(s => s.Length > 0).Distinct().ToArray();
        WindowsDirectory = NormalizeDirectory(windowsDirectory);
        ProtectedProcessNames = protectedProcessNames.Select(s => s.Trim().ToLowerInvariant()).ToHashSet();
        CriticalServiceNames = criticalServiceNames.Select(s => s.Trim().ToLowerInvariant()).ToHashSet();
        ProtectedServiceNames = (protectedServiceNames ?? DefaultProtectedServiceNames)
            .Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToHashSet();
        ProtectedRegistryKeys = protectedRegistryKeys.Select(NormalizeRegistry).Where(s => s.Length > 0).Distinct().ToArray();
        WholeSubtreeRegistryRoots = wholeSubtreeRegistryRoots.Select(s => s.Trim().ToLowerInvariant()).ToHashSet();
        CommandDenyList = commandDenyList.Select(s => s.Trim().ToLowerInvariant()).ToHashSet();
        CommandDenyStems = DefaultCommandDenyStems.Select(s => s.Trim().ToLowerInvariant()).ToHashSet();
        ValueDeleteAllowedKeys = (valueDeleteAllowedKeys ?? DefaultValueDeleteAllowedKeys)
            .Select(NormalizeRegistry).Where(s => s.Length > 0).ToHashSet();
        ProtectedValueKeys = (protectedValueKeys ?? DefaultProtectedValueKeys)
            .Select(NormalizeRegistry).Where(s => s.Length > 0).Distinct().ToArray();
        WriteProtectedRoots = (writeProtectedRoots ?? Array.Empty<string>())
            .Select(NormalizeDirectory).Where(s => s.Length > 0).Distinct().ToArray();
        UsersRoot = NormalizeDirectory(usersRoot);
        CurrentUserProfile = NormalizeDirectory(currentUserProfile);
    }

    /// <summary>Trim trailing separators and lowercase a directory path for comparison.</summary>
    public static string NormalizeDirectory(string? path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : path.TrimEnd('\\', '/').ToLowerInvariant();

    /// <summary>Trim leading/trailing backslashes and lowercase a registry subkey path.</summary>
    public static string NormalizeRegistry(string? subKey)
        => string.IsNullOrWhiteSpace(subKey) ? string.Empty : subKey.Trim().Trim('\\').ToLowerInvariant();

    /// <summary>Build the policy from the running system's well-known folders.</summary>
    public static ProtectedResources ForCurrentSystem()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string usersRoot = Path.GetDirectoryName(userProfile) ?? string.Empty;

        var dirs = new[] { windows, programFiles, programFilesX86, programData, userProfile, usersRoot };

        return new ProtectedResources(
            protectedDirectories: dirs,
            windowsDirectory: windows,
            protectedProcessNames: DefaultProtectedProcessNames,
            criticalServiceNames: DefaultCriticalServiceNames,
            protectedRegistryKeys: DefaultProtectedRegistryKeys,
            wholeSubtreeRegistryRoots: DefaultWholeSubtreeRegistryRoots,
            commandDenyList: DefaultCommandDenyList,
            valueDeleteAllowedKeys: DefaultValueDeleteAllowedKeys,
            protectedValueKeys: DefaultProtectedValueKeys,
            writeProtectedRoots: new[] { windows, programFiles, programFilesX86, programData },
            usersRoot: usersRoot,
            currentUserProfile: userProfile,
            protectedServiceNames: DefaultProtectedServiceNames);
    }

    // ---- Default policy tables (used by ForCurrentSystem; tests can override per-case) ----

    public static readonly string[] DefaultProtectedProcessNames =
    {
        "system", "registry", "idle", "smss", "csrss", "wininit", "winlogon", "services",
        "lsass", "lsaiso", "fontdrvhost", "dwm", "svchost", "conhost", "sihost", "explorer",
        "audiodg", "msmpeng", "securityhealthservice",
    };

    public static readonly string[] DefaultCriticalServiceNames =
    {
        "rpcss", "rpceptmapper", "dcomlaunch", "dhcp", "dnscache", "bfe", "mpssvc", "windefend",
        "winmgmt", "eventlog", "plugplay", "power", "profsvc", "lanmanserver", "lanmanworkstation",
        "schedule", "gpsvc", "cryptsvc", "trustedinstaller", "samss", "lsm", "nsi",
        "brokerinfrastructure", "systemeventsbroker", "coremessaging", "usermanager",
    };

    /// <summary>
    /// Security / update / search services protected from <c>Disable</c> and <c>Delete</c> (a transient
    /// <c>Stop</c> is allowed). These are not on <see cref="DefaultCriticalServiceNames"/> (the machine still
    /// boots without them) but persistently turning them off is a known weakening/persistence move (L1).
    /// </summary>
    public static readonly string[] DefaultProtectedServiceNames =
    {
        // Microsoft Defender (network inspection + EDR sensor + Security Center + health).
        // (windefend / mpssvc / bfe are already boot-critical — blocked for every op by CriticalServiceNames.)
        "wdnissvc", "sense", "wscsvc", "securityhealthservice",
        // Windows Update + delivery
        "wuauserv", "bits", "usosvc", "waasmedicsvc", "dosvc",
        // Search / indexing
        "wsearch",
    };

    public static readonly string[] DefaultProtectedRegistryKeys =
    {
        "software",
        "software\\microsoft",
        "software\\microsoft\\windows",
        "software\\microsoft\\windows\\currentversion",
        "software\\microsoft\\windows nt",
        "software\\microsoft\\windows nt\\currentversion",
        // High-value boot/logon keys whose *key* delete must be refused outright.
        "software\\microsoft\\windows nt\\currentversion\\winlogon",
        "software\\microsoft\\windows nt\\currentversion\\image file execution options",
        "software\\classes",
        "software\\policies",
        "software\\microsoft\\windows\\currentversion\\policies",
        "software\\wow6432node",
        "software\\wow6432node\\microsoft",
        "software\\wow6432node\\microsoft\\windows",
        "software\\wow6432node\\microsoft\\windows\\currentversion",
    };

    /// <summary>Keys where a single value-delete is allowed despite sitting under a protected subtree.</summary>
    public static readonly string[] DefaultValueDeleteAllowedKeys =
    {
        "software\\microsoft\\windows\\currentversion\\run",
        "software\\microsoft\\windows\\currentversion\\runonce",
        "software\\microsoft\\windows\\currentversion\\runservices",
        "software\\microsoft\\windows\\currentversion\\runservicesonce",
        "software\\wow6432node\\microsoft\\windows\\currentversion\\run",
        "software\\wow6432node\\microsoft\\windows\\currentversion\\runonce",
    };

    /// <summary>Keys whose values must never be deleted (boot/logon-critical).</summary>
    public static readonly string[] DefaultProtectedValueKeys =
    {
        "software\\microsoft\\windows nt\\currentversion\\winlogon",
        "software\\microsoft\\windows nt\\currentversion\\image file execution options",
        "software\\microsoft\\windows nt\\currentversion\\windows", // AppInit_DLLs
        "software\\microsoft\\windows\\currentversion\\policies",
        "software\\policies",
        "software\\wow6432node\\microsoft\\windows nt\\currentversion\\winlogon",
    };

    public static readonly string[] DefaultWholeSubtreeRegistryRoots =
    {
        "system", "security", "sam", "hardware", "bcd00000000",
    };

    public static readonly string[] DefaultCommandDenyList =
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe",
        "mshta.exe", "regedit.exe", "reg.exe", "regsvr32.exe",
    };

    /// <summary>Interpreter + LOLBin stems (no extension) blocked for <c>CommandAction</c>.</summary>
    public static readonly string[] DefaultCommandDenyStems =
    {
        // shells / script hosts
        "cmd", "command", "powershell", "pwsh", "wscript", "cscript", "mshta", "hh",
        // registry / scripting tools
        "regedit", "reg", "regsvr32", "regsvcs", "regasm",
        // living-off-the-land binaries that download/execute arbitrary code
        "rundll32", "installutil", "msbuild", "certutil", "bitsadmin", "wmic",
        "forfiles", "mavinject", "ieexec", "presentationhost", "msxsl", "wsl",
        // network / shell helpers
        "curl", "wget", "explorer",
    };
}

using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Safety;

/// <summary>
/// The single gate every destructive action passes through. It is pure policy: given a
/// <see cref="ProtectedResources"/> table and an <see cref="IPathCanonicalizer"/>, it decides whether
/// each typed action may run. It fails closed — anything it cannot positively verify is blocked.
///
/// File deletes are checked against BOTH the canonical (junction/symlink-resolved) path and the
/// literal normalized path, so a reparse-point trick cannot smuggle a protected directory past the
/// gate (spec §3).
/// </summary>
public sealed class SafetyGate : ISafetyGate
{
    private readonly ProtectedResources _policy;
    private readonly IPathCanonicalizer _canonicalizer;

    public SafetyGate(ProtectedResources policy, IPathCanonicalizer canonicalizer)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _canonicalizer = canonicalizer ?? throw new ArgumentNullException(nameof(canonicalizer));
    }

    public SafetyVerdict Evaluate(PlannedAction action) => action switch
    {
        FileDeleteAction f => EvaluateFileDelete(f.Path),
        RegistryDeleteAction r => EvaluateRegistryDelete(r),
        ServiceDeleteAction s => EvaluateService(s),
        TaskDeleteAction t => EvaluateTask(t),
        CommandAction c => EvaluateCommand(c),
        CopyAction cp => EvaluateWriteTarget(cp.Destination, "copy destination"),
        RestoreMergeAction rm => EvaluateWriteTarget(rm.Destination, "restore destination"),
        // Creating a System Restore point is a pure system call that ADDS rollback state — it mutates no
        // protected resource, so it is allowed outright (UI decision §5(a)). This is an EXPLICIT arm, not a
        // catch-all: an unknown/unmodeled action type still falls through to the fail-closed `_ => Block`.
        CreateRestorePointAction => SafetyVerdict.Allow("create restore point (pure system call)"),
        null => SafetyVerdict.Block("null action"),
        _ => SafetyVerdict.Block($"unknown action type: {action.GetType().Name}"),
    };

    public PlanValidationResult Validate(OperationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var results = plan.Actions
            .Select(a => new ActionVerdict(a, Evaluate(a)))
            .ToArray();
        return new PlanValidationResult(results.All(r => r.Verdict.Allowed), results);
    }

    // ---- File deletes: defense in depth over canonical + literal path ----

    private SafetyVerdict EvaluateFileDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return SafetyVerdict.Block("empty path");

        CanonicalPath canon = _canonicalizer.Canonicalize(path);

        // A junction/symlink we could not resolve to a real target is untrustworthy → block.
        if (canon.IsReparsePoint && !canon.Resolved)
            return SafetyVerdict.Block("unresolvable reparse point (junction/symlink)");

        // 1) the resolved target must not be protected (catches junction → ProgramData tricks)
        var canonVerdict = EvaluatePathString(canon.FinalPath, viaReparse: canon.IsReparsePoint);
        if (!canonVerdict.Allowed)
            return canonVerdict;

        // 2) the literal path must not be protected either (defense in depth)
        if (!TryNormalizeFull(path, out string literal))
            return SafetyVerdict.Block("path could not be normalized");

        return EvaluatePathString(literal, viaReparse: false);
    }

    private SafetyVerdict EvaluateWriteTarget(string destination, string label)
    {
        if (string.IsNullOrWhiteSpace(destination))
            return SafetyVerdict.Block($"empty {label}");

        // Resolve junctions/symlinks so a link can't redirect a write into a protected tree.
        CanonicalPath canon = _canonicalizer.Canonicalize(destination);
        if (canon.IsReparsePoint && !canon.Resolved)
            return SafetyVerdict.Block("unresolvable reparse point (junction/symlink)");

        var canonVerdict = EvaluateWritePathString(canon.FinalPath);
        if (!canonVerdict.Allowed)
            return canonVerdict;

        if (!TryNormalizeFull(destination, out string literal))
            return SafetyVerdict.Block($"{label} could not be normalized");

        return EvaluateWritePathString(literal);
    }

    /// <summary>
    /// Write-target policy: the delete policy PLUS the stricter rule that nothing may be CREATED under
    /// the Windows tree, Program Files, ProgramData, or another user's profile (DLL-plant / all-users
    /// startup persistence defense, spec §3).
    /// </summary>
    private SafetyVerdict EvaluateWritePathString(string path)
    {
        SafetyVerdict baseVerdict = EvaluatePathString(path, viaReparse: false);
        if (!baseVerdict.Allowed)
            return baseVerdict;

        string norm = ProtectedResources.NormalizeDirectory(path);

        foreach (string root in _policy.WriteProtectedRoots)
        {
            if (norm == root || norm.StartsWith(root + "\\", StringComparison.Ordinal))
                return SafetyVerdict.Block("inside a write-protected system directory");
        }

        string usersRoot = _policy.UsersRoot;
        if (usersRoot.Length > 0 && (norm == usersRoot || norm.StartsWith(usersRoot + "\\", StringComparison.Ordinal)))
        {
            string profile = _policy.CurrentUserProfile;
            bool underCurrentProfile = profile.Length > 0
                && (norm == profile || norm.StartsWith(profile + "\\", StringComparison.Ordinal));
            if (!underCurrentProfile)
                return SafetyVerdict.Block("another user's profile");
        }

        return SafetyVerdict.Allow("write target not protected");
    }

    /// <summary>Core path policy: drive roots, the Windows tree, and the protected directory list.</summary>
    private SafetyVerdict EvaluatePathString(string path, bool viaReparse)
    {
        string norm = ProtectedResources.NormalizeDirectory(path);
        if (norm.Length == 0)
            return SafetyVerdict.Block("unresolved path");

        // Only local, drive-rooted paths are operated on. UNC / device paths fail closed.
        if (!IsLocalRootedPath(norm))
            return SafetyVerdict.Block("non-local or unrooted path");

        if (IsDriveRoot(norm))
            return SafetyVerdict.Block("drive root");

        string win = _policy.WindowsDirectory;
        if (win.Length > 0 && (norm == win || norm.StartsWith(win + "\\", StringComparison.Ordinal)))
            return SafetyVerdict.Block("inside the Windows directory");

        foreach (string dir in _policy.ProtectedDirectories)
        {
            if (norm == dir)
                return SafetyVerdict.Block("protected system directory");
            // Deleting an ancestor of a protected directory would take the protected dir with it.
            if (dir.StartsWith(norm + "\\", StringComparison.Ordinal))
                return SafetyVerdict.Block("ancestor of a protected directory");
        }

        string suffix = viaReparse ? " (resolved via reparse point)" : string.Empty;
        return SafetyVerdict.Allow("path not protected" + suffix);
    }

    // ---- Registry ----

    private SafetyVerdict EvaluateRegistryDelete(RegistryDeleteAction r)
    {
        // Re-root the subkey per hive so the software-rooted policy tables line up, and FAIL CLOSED on any
        // hive the policy does not model (HKCC and anything unknown) — the tables would otherwise pass through
        // to Allow for those hives (spec §1/§10).
        if (!TryEffectiveRegistryPath(r.Hive, r.SubKeyPath, out string sub, out SafetyVerdict? hiveBlock))
            return hiveBlock!;

        bool isValueDelete = r.ValueName is not null;

        if (sub.Length == 0)
        {
            // Deleting a hive root key is always refused; a stray value at the root is harmless.
            return isValueDelete
                ? SafetyVerdict.Allow("value at hive root")
                : SafetyVerdict.Block("registry hive root");
        }

        string first = sub.Split('\\', 2)[0];
        if (_policy.WholeSubtreeRegistryRoots.Contains(first))
            return SafetyVerdict.Block($"protected registry subtree ({first.ToUpperInvariant()})");

        if (isValueDelete)
            return EvaluateRegistryValueDelete(sub);

        foreach (string pk in _policy.ProtectedRegistryKeys)
        {
            if (sub == pk)
                return SafetyVerdict.Block("protected registry key");
            if (pk.StartsWith(sub + "\\", StringComparison.Ordinal))
                return SafetyVerdict.Block("ancestor of a protected registry key");
        }

        return SafetyVerdict.Allow("registry key not protected");
    }

    /// <summary>
    /// Map a (hive, subkey) to the software-rooted form the policy tables expect. Returns false (with a Block
    /// verdict) for hives the policy does not model, so the gate fails closed instead of allowing.
    /// </summary>
    private static bool TryEffectiveRegistryPath(RegistryHive hive, string? subKey, out string effective, out SafetyVerdict? block)
    {
        block = null;
        string sub = ProtectedResources.NormalizeRegistry(subKey);
        switch (hive)
        {
            case RegistryHive.LocalMachine:
            case RegistryHive.CurrentUser:
                effective = sub;
                return true;
            case RegistryHive.ClassesRoot:
                // HKCR is the merged Classes view — treat as SOFTWARE\Classes\<sub>.
                effective = sub.Length == 0 ? "software\\classes" : "software\\classes\\" + sub;
                return true;
            case RegistryHive.Users:
                // HKU paths are "<SID>\Software\…"; strip the SID and evaluate the per-user remainder.
                if (sub.Length == 0)
                {
                    effective = string.Empty;
                    block = SafetyVerdict.Block("HKU root");
                    return false;
                }
                int slash = sub.IndexOf('\\');
                if (slash < 0)
                {
                    effective = string.Empty;
                    block = SafetyVerdict.Block("HKU without a user subkey");
                    return false;
                }
                effective = sub[(slash + 1)..];
                return true;
            default:
                effective = string.Empty;
                block = SafetyVerdict.Block($"unmodeled registry hive ({hive})");
                return false;
        }
    }

    /// <summary>
    /// Value deletes are allowed broadly (vendor settings, startup entries) EXCEPT inside a small set
    /// of boot/logon-critical keys (Winlogon, IFEO, policies). Removing e.g. Winlogon\Userinit can make
    /// Windows unbootable, so those are blocked unless the key is explicitly on the startup allowlist.
    /// </summary>
    private SafetyVerdict EvaluateRegistryValueDelete(string sub)
    {
        if (_policy.ValueDeleteAllowedKeys.Contains(sub))
            return SafetyVerdict.Allow("value delete in an allowed startup key");

        foreach (string pvk in _policy.ProtectedValueKeys)
        {
            if (sub == pvk || sub.StartsWith(pvk + "\\", StringComparison.Ordinal))
                return SafetyVerdict.Block("value inside a boot/logon-critical registry key");
        }

        return SafetyVerdict.Allow("registry value delete");
    }

    // ---- Services / tasks / commands ----

    private SafetyVerdict EvaluateService(ServiceDeleteAction s)
    {
        string name = (s.ServiceName ?? string.Empty).Trim().ToLowerInvariant();
        if (name.Length == 0)
            return SafetyVerdict.Block("empty service name");

        // Critical-to-boot services: every operation (Stop/Disable/Delete) is refused.
        if (_policy.CriticalServiceNames.Contains(name))
            return SafetyVerdict.Block("critical Windows service");

        // Security / update / search services: a transient Stop is allowed, but persistently turning them
        // off (Disable) or removing them (Delete) is refused — the gate now honors the Operation (L1).
        if (_policy.ProtectedServiceNames.Contains(name)
            && s.Operation is ServiceOperation.Disable or ServiceOperation.Delete)
            return SafetyVerdict.Block($"security/update service must stay enabled (no {s.Operation})");

        return SafetyVerdict.Allow("service not critical");
    }

    private SafetyVerdict EvaluateTask(TaskDeleteAction t)
    {
        string path = (t.TaskPath ?? string.Empty).Trim().Replace('/', '\\').ToLowerInvariant();
        if (path.Length == 0)
            return SafetyVerdict.Block("empty task path");
        if (!path.StartsWith('\\'))
            path = "\\" + path;
        if (path == "\\microsoft" || path == "\\microsoft\\windows"
            || path.StartsWith("\\microsoft\\windows\\", StringComparison.Ordinal))
            return SafetyVerdict.Block("protected OS scheduled task");
        return SafetyVerdict.Allow("task not protected");
    }

    private SafetyVerdict EvaluateCommand(CommandAction c)
    {
        string fileName = c.FileName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
            return SafetyVerdict.Block("empty command");

        // UNC / device executable paths are never launched.
        if (fileName.StartsWith(@"\\", StringComparison.Ordinal))
            return SafetyVerdict.Block("UNC/device command path");

        string stem = CommandStem(fileName);
        if (stem.Length == 0)
            return SafetyVerdict.Block("invalid command path");

        if (_policy.CommandDenyStems.Contains(stem) || _policy.CommandDenyList.Contains(stem + ".exe"))
            return SafetyVerdict.Block("interpreter/LOLBin not allowed (no shell-string execution)");

        bool isMsiexec = stem == "msiexec";

        // Every command except the trusted System32 msiexec must be an absolute, rooted path so the OS
        // (especially under elevation/runas) can never PATH-search the executable (PATH-hijack defense).
        if (!isMsiexec && !Path.IsPathFullyQualified(fileName))
            return SafetyVerdict.Block("command must be an absolute path (no PATH search)");

        // Argument hygiene for every command: no UNC sources, URLs, encoded commands, or script URIs.
        foreach (string arg in c.Arguments)
        {
            SafetyVerdict argVerdict = InspectArgument(arg);
            if (!argVerdict.Allowed)
                return argVerdict;
        }

        // msiexec is only ever allowed as an UNINSTALL (no /i, /a, /j, /p, /f*, TRANSFORMS=).
        if (isMsiexec)
            return EvaluateMsiexecArguments(c.Arguments);

        return SafetyVerdict.Allow("command allowed");
    }

    /// <summary>Normalized executable stem: file name, trailing dots/spaces stripped, extension removed, lowercased.</summary>
    private static string CommandStem(string fileName)
    {
        string name;
        try
        {
            name = Path.GetFileName(fileName.Trim());
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        name = name.TrimEnd('.', ' ');
        int dot = name.LastIndexOf('.');
        if (dot > 0)
            name = name.Substring(0, dot);
        return name.ToLowerInvariant();
    }

    private static SafetyVerdict InspectArgument(string? arg)
    {
        string a = arg ?? string.Empty;
        string lower = a.ToLowerInvariant();
        if (a.Contains(@"\\", StringComparison.Ordinal))
            return SafetyVerdict.Block("UNC path in command arguments");
        if (lower.Contains("://", StringComparison.Ordinal))
            return SafetyVerdict.Block("URL in command arguments");
        if (lower.Contains("-enc", StringComparison.Ordinal) || lower.Contains("encodedcommand", StringComparison.Ordinal))
            return SafetyVerdict.Block("encoded command in arguments");
        if (lower.StartsWith("javascript:", StringComparison.Ordinal) || lower.StartsWith("vbscript:", StringComparison.Ordinal))
            return SafetyVerdict.Block("script URI in arguments");
        return SafetyVerdict.Allow("argument ok");
    }

    private static SafetyVerdict EvaluateMsiexecArguments(IReadOnlyList<string> args)
    {
        string joined = string.Join(" ", args).ToLowerInvariant();
        if (joined.Contains("transforms=", StringComparison.Ordinal))
            return SafetyVerdict.Block("msiexec TRANSFORMS not allowed");

        foreach (string arg in args)
        {
            string t = arg.TrimStart('/', '-').ToLowerInvariant();
            // install / admin-install / advertise / patch / repair-reinstall verbs are forbidden (uninstall only).
            if (t.StartsWith('i') || t.StartsWith('a') || t.StartsWith('j') || t.StartsWith('p') || t.StartsWith('f'))
                return SafetyVerdict.Block("msiexec install/repair verb not allowed (uninstall only)");
        }

        bool hasUninstallVerb = args.Any(a =>
        {
            string t = a.TrimStart('/', '-').ToLowerInvariant();
            return t.StartsWith('x') || t.StartsWith("uninstall");
        });
        bool hasProductCode = args.Any(a => a.Contains('{') && a.Contains('}'));

        if (!hasUninstallVerb)
            return SafetyVerdict.Block("msiexec without an uninstall verb");
        if (!hasProductCode)
            return SafetyVerdict.Block("msiexec uninstall without a product code");

        return SafetyVerdict.Allow("msiexec uninstall");
    }

    // ---- Path helpers ----

    private static bool IsLocalRootedPath(string norm)
        => norm.Length >= 2 && char.IsLetter(norm[0]) && norm[1] == ':';

    private static bool IsDriveRoot(string norm)
        => norm.Length == 2 && char.IsLetter(norm[0]) && norm[1] == ':';

    /// <summary>
    /// Normalize the LITERAL path for the defense-in-depth branch. Uses the canonicalizer's
    /// <see cref="IPathCanonicalizer.ExpandLongPath"/> (8.3 short-name expansion + trailing dot/space strip)
    /// instead of a bare <see cref="Path.GetFullPath(string)"/>, so a short-name/trailing-dot alias of a
    /// protected directory still normalizes onto the protected literal (L12).
    /// </summary>
    private bool TryNormalizeFull(string path, out string normalized)
    {
        try
        {
            normalized = ProtectedResources.NormalizeDirectory(_canonicalizer.ExpandLongPath(path));
            return normalized.Length > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = string.Empty;
            return false;
        }
    }
}

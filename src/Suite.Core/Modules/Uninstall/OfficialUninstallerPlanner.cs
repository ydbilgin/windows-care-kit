using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>
/// Builds the plan that runs an app's vendor-provided uninstaller. The uninstall string is parsed
/// into a structured <see cref="CommandAction"/> (no shell string), so the gate can vet the executable
/// and it later runs via <c>ProcessStartInfo.ArgumentList</c> (spec §1.1, §4).
/// </summary>
public static class OfficialUninstallerPlanner
{
    /// <summary>Returns null when the app has no usable uninstall string.</summary>
    public static OperationPlan? Build(InstalledApp app, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(app);

        string? raw = !string.IsNullOrWhiteSpace(app.QuietUninstallString)
            ? app.QuietUninstallString
            : app.UninstallString;

        ParsedUninstallCommand? parsed = UninstallStringParser.Parse(raw);
        if (parsed is null || !parsed.IsValid)
            return null;

        // A NON-MSI uninstaller whose executable is a script/shell-container (.bat/.cmd/.ps1/.vbs/…) is never a
        // legitimate vendor uninstaller (L1-L7 shapes are all real executables) — it is the registry-string
        // wrapper-script attack. Do NOT build the official-uninstaller action; return null so the caller falls
        // back to leftover-cleanup. (.bat/.cmd ARE rejected here, unlike the command gate's npm carve-out,
        // because npm flows through InstallPlanner, not this planner.)
        if (!parsed.IsMsi && HasDangerousUninstallerExtension(parsed.FileName))
            return null;

        // Pin msiexec to the trusted System32 binary (registry stores it bare). The gate validates that the
        // arguments are an uninstall (/x{GUID}) only.
        string fileName = parsed.IsMsi
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe")
            : parsed.FileName;

        bool requiresElevation = app.IsMachineWide;

        // Command-policy Phase 2 (Fix 1): carry the OfficialUninstaller profile and the canonical install
        // directory the executable must sit under when elevated. The GATE is the authoritative decision point and
        // does the real containment check on the 8.3-EXPANDED exe path. Here we only PRE-FLIGHT the
        // expansion-INDEPENDENT "can this EVER anchor?" question (a usable local-rooted InstallLocation exists, OR
        // the NSIS carve-out), so an elevated uninstaller that can never anchor returns null → the wizard's manual
        // fallback (no broken/no-op auto action). We deliberately do NOT run the path-containment check here: the
        // planner has no IPathCanonicalizer, so containing the RAW exe would over-block a registry-stored 8.3 path
        // (C:\PROGRA~1\App\unins000.exe) the gate WOULD allow after expansion (cx REJECT). The planner is thus
        // never STRICTER than the gate. msiexec is System32-pinned and handled by the gate's System32 special-case.
        string? allowedRoot = CanonicalizeRoot(app.InstallLocation);

        if (requiresElevation && !parsed.IsMsi
            && !CommandPolicy.CanPossiblyAnchorElevatedUninstaller(parsed.FileName, allowedRoot))
        {
            // Elevated AND anchoring is impossible regardless of expansion (no usable InstallLocation AND not the
            // NSIS carve-out) → manual fallback, not a silent failure. (If a usable root exists, we build the action
            // and let the gate make the authoritative contained-or-block decision on the expanded path.)
            return null;
        }

        var command = new CommandAction
        {
            FileName = fileName,
            Arguments = parsed.Arguments,
            RequiresElevation = requiresElevation,
            Profile = CommandPolicyProfile.OfficialUninstaller,
            AllowedExecutableRoot = parsed.IsMsi ? null : allowedRoot,
            Description = $"Run the official uninstaller for {app.DisplayName}",
            Reason = "Vendor-provided uninstaller from the registry UninstallString",
            Risk = RiskLevel.Medium,
            Undo = UndoCapability.None,
        };

        return new OperationPlan($"Uninstall {app.DisplayName}", "uninstall", new[] { command }, utc);
    }

    /// <summary>
    /// Canonicalize the app's <c>InstallLocation</c> into the anchor root the gate keys off (Command-policy
    /// Phase 2). Quotes/whitespace are trimmed and <c>..</c>/<c>.</c> collapsed via <see cref="System.IO.Path.GetFullPath(string)"/>;
    /// a missing or malformed location returns null (no anchor). The gate independently re-checks the exe against
    /// this root (and rejects UNC / un-rooted roots), so this is a best-effort normalization only.
    /// </summary>
    private static string? CanonicalizeRoot(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
            return null;
        string p = installLocation.Trim().Trim('"').Trim();
        if (p.Length == 0)
            return null;
        try { p = System.IO.Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return p.TrimEnd('\\');
    }

    private static readonly HashSet<string> DangerousUninstallerExtensions =
        new(ProtectedResources.DefaultUninstallerDeniedExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the parsed uninstaller's file name ends in a script/dangerous extension (no legit uninstaller does).</summary>
    private static bool HasDangerousUninstallerExtension(string fileName)
    {
        string ext = System.IO.Path.GetExtension(fileName.Trim().TrimEnd('.', ' '));
        return ext.Length > 0 && DangerousUninstallerExtensions.Contains(ext);
    }
}

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

        var command = new CommandAction
        {
            FileName = fileName,
            Arguments = parsed.Arguments,
            RequiresElevation = app.IsMachineWide,
            Description = $"Run the official uninstaller for {app.DisplayName}",
            Reason = "Vendor-provided uninstaller from the registry UninstallString",
            Risk = RiskLevel.Medium,
            Undo = UndoCapability.None,
        };

        return new OperationPlan($"Uninstall {app.DisplayName}", "uninstall", new[] { command }, utc);
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

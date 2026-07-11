using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Thrown when a <see cref="CommandAction"/> process started but exited non-zero. Carries the exit code
/// so the executor can record a <c>Failed</c> result (a non-zero exit is a failure, not an OS-level
/// exception).
/// </summary>
public sealed class ProcessExitCodeException : Exception
{
    /// <summary>The process exit code.</summary>
    public int ExitCode { get; }

    public ProcessExitCodeException(string fileName, int exitCode)
        : base($"'{fileName}' exited with code {exitCode}.")
        => ExitCode = exitCode;
}

/// <summary>
/// Runs an executable with a structured <see cref="ProcessStartInfo.ArgumentList"/> — NEVER a joined
/// argument string and never a shell string (spec §1.1/§4). Elevation (<c>Verb="runas"</c>) is used
/// ONLY when <see cref="CommandAction.RequiresElevation"/> is true; this is the one place in the codebase
/// where that verb may appear. The gate has already denied <c>cmd/powershell/…</c> (the command deny-list).
/// </summary>
public sealed class ProcessAdapter : IProcessAdapter
{
    /// <inheritdoc />
    public void Run(CommandAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Defense in depth (the gate already enforces this): never let ShellExecute PATH-search an
        // unrooted name under elevation — that is a privilege-escalation primitive.
        if (action.RequiresElevation && !Path.IsPathFullyQualified(action.FileName))
            throw new InvalidOperationException(
                $"Refusing to run an elevated command without an absolute path: {action.FileName}");

        if (action.RequiresElevation)
            EnsureElevatableOwnership(action.FileName);

        var psi = new ProcessStartInfo
        {
            FileName = action.FileName,
        };

        // One entry per argument — .NET quotes each correctly, including on the elevation (ShellExecute) path.
        foreach (string arg in action.Arguments)
            psi.ArgumentList.Add(arg);

        if (action.RequiresElevation)
        {
            // Elevation requires ShellExecute; the UAC verb is "runas".
            psi.UseShellExecute = true;
            psi.Verb = "runas";
        }
        else
        {
            // Non-elevated: ShellExecute off so output could be captured and ArgumentList is honored.
            psi.UseShellExecute = false;
        }

#pragma warning disable RS0030 // Sanctioned process launch (Suite.Execution): the only place a CommandAction is run (optionally elevated).
        Process process;
        try
        {
            process = Process.Start(psi)
                      ?? throw new InvalidOperationException($"Failed to start process: {action.FileName}");
        }
        catch (Win32Exception ex)
        {
            // Includes the user cancelling the UAC prompt (ERROR_CANCELLED 1223) on the elevation path.
            throw new InvalidOperationException($"Could not start '{action.FileName}': {ex.Message}", ex);
        }

        using (process)
        {
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new ProcessExitCodeException(action.FileName, process.ExitCode);
        }
#pragma warning restore RS0030
    }

    /// <summary>
    /// Before a <c>runas</c> elevation, confirm the target executable is owned by a trusted principal
    /// (Administrators / SYSTEM / TrustedInstaller). A user-writable, user-owned binary must never be elevated
    /// (S3 defense in depth: the gate already refuses user-writable-root elevation, but the adapter re-checks
    /// ownership at the moment of launch). Fails closed: if the owner cannot be resolved, elevation is refused.
    /// </summary>
    private static void EnsureElevatableOwnership(string fileName)
    {
        if (!File.Exists(fileName))
            throw new InvalidOperationException($"Refusing to elevate a missing executable: {fileName}");

        IdentityReference? owner;
        try
        {
            owner = new FileInfo(fileName).GetAccessControl()
                .GetOwner(typeof(SecurityIdentifier));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Refusing to elevate '{fileName}': ownership could not be verified ({ex.GetType().Name}).", ex);
        }

        if (owner is not SecurityIdentifier sid || !IsTrustedOwner(sid))
            throw new InvalidOperationException(
                $"Refusing to elevate '{fileName}': not owned by a trusted system principal.");
    }

    private static bool IsTrustedOwner(SecurityIdentifier sid)
        => sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
           || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
           // TrustedInstaller: S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464
           || string.Equals(sid.Value,
                "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", StringComparison.OrdinalIgnoreCase);
}

using System.IO;
using Microsoft.Win32;
using WindowsCareKit.Core.Modules.Backup;

namespace WindowsCareKit.Win32;

/// <summary>
/// Expands <c>%ENV%</c> tokens in a manifest <c>source</c> path and resolves the user shell folders that
/// can be OneDrive-redirected (Desktop / Documents / Pictures / …) by reading the real targets from the
/// per-user <c>User Shell Folders</c> key (spec §1.3: resolve Known Folders incl. OneDrive redirection).
/// Read-only: it only reads the environment and a per-user registry value, it never writes.
/// </summary>
public sealed class Win32EnvironmentExpander : IEnvironmentExpander
{
    private const string ShellFoldersKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

    // Maps the %USERPROFILE%\<name> tail we see in the manifests to its User-Shell-Folders value name,
    // so a OneDrive-redirected Desktop/Documents resolves to the real (redirected) location.
    private static readonly (string Tail, string ShellValue)[] RedirectableFolders =
    {
        (@"\Desktop", "Desktop"),
        (@"\Documents", "Personal"),
        (@"\Pictures", "My Pictures"),
        (@"\Music", "My Music"),
        (@"\Videos", "My Video"),
        (@"\Downloads", "{374DE290-123F-4565-9164-39C4925E467B}"),
    };

    /// <inheritdoc />
    public string Expand(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        string expanded = Environment.ExpandEnvironmentVariables(path);

        // After %USERPROFILE% expansion, redirect the known shell folders if the user moved them (OneDrive).
        string? userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrEmpty(userProfile))
        {
            foreach ((string tail, string shellValue) in RedirectableFolders)
            {
                string canonical = userProfile + tail;
                if (expanded.StartsWith(canonical, StringComparison.OrdinalIgnoreCase)
                    && IsExactSegment(expanded, canonical))
                {
                    string? redirected = ReadShellFolder(shellValue);
                    if (!string.IsNullOrEmpty(redirected)
                        && !string.Equals(redirected, canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        string remainder = expanded.Substring(canonical.Length);
                        return redirected + remainder;
                    }
                    break;
                }
            }
        }

        return expanded;
    }

    /// <summary>True when <paramref name="folder"/> is a whole path segment of <paramref name="full"/> (not a prefix of a longer name).</summary>
    private static bool IsExactSegment(string full, string folder)
        => full.Length == folder.Length
           || full[folder.Length] == Path.DirectorySeparatorChar
           || full[folder.Length] == Path.AltDirectorySeparatorChar;

    /// <summary>Read and env-expand a <c>User Shell Folders</c> value (its data may itself contain %USERPROFILE%).</summary>
    private static string? ReadShellFolder(string valueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ShellFoldersKey);
            if (key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw
                || raw.Length == 0)
                return null;
            return Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(raw));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

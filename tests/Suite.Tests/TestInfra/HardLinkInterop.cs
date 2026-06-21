using System.Runtime.InteropServices;

namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// Thin <c>CreateHardLink</c> P/Invoke wrapper shared by the hard-link capability probe
/// (<see cref="HostCapabilities.HardLinkSupported"/>) and the CopyAdapter hard-link exclusion test, so both
/// create a real NTFS hard link the same way. A hard link needs no elevation but requires the source and link
/// to be on the same NTFS volume; some environments (e.g. a FAT/exFAT temp drive) disallow it.
/// </summary>
internal static class HardLinkInterop
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>Create a hard link <paramref name="link"/> aliasing the existing file <paramref name="target"/>;
    /// false if the OS refused (cross-volume, non-NTFS, or unsupported).</summary>
    public static bool TryCreateHardLink(string link, string target)
    {
        try
        {
            return CreateHardLink(link, target, IntPtr.Zero) && File.Exists(link);
        }
        catch
        {
            return false;
        }
    }
}

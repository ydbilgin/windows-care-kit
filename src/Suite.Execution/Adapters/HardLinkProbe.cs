using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Detects whether a file has more than one hard link (<c>nNumberOfLinks &gt; 1</c>). A hard link is a second
/// directory entry that points at the SAME on-disk file as another path on the same volume; both names are
/// equal aliases. <c>GetFinalPathNameByHandle</c> (what <see cref="WindowsCareKit.Win32.Win32PathCanonicalizer"/>
/// uses) canonically returns the path you opened — it does NOT de-alias a hard link to the "secret" name — so a
/// hard link under an innocuous leaf (e.g. <c>settings.json</c> hard-linked to a browser's <c>Login Data</c>)
/// would otherwise sail past the leaf-name secret filter. <see cref="CopyAdapter"/> treats any multi-linked file
/// as not-allowed (fail-safe), refusing to copy it.
///
/// <para>All P/Invokes live in this assembly so the assembly-level
/// <c>[DefaultDllImportSearchPaths(System32)]</c> (see <c>DllImportSecurity.cs</c>) applies — keeps CA5392 green
/// without per-method attributes.</para>
/// </summary>
internal static class HardLinkProbe
{
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;

    /// <summary>
    /// True when <paramref name="path"/> is a file with more than one hard link. Fail-safe: if the link count
    /// CANNOT be determined (file unopenable, query failed), returns <see langword="true"/> so the caller refuses
    /// the copy rather than risk leaking a hard-linked secret it could not vet. Returns <see langword="false"/>
    /// only when it positively confirmed a single link.
    /// </summary>
    public static bool IsMultiLinked(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true; // no vettable path → fail safe (refuse), consistent with this predicate's contract

        try
        {
            using SafeFileHandle handle = CreateFile(
                path,
                0, // query metadata only; no read/write access needed
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (handle.IsInvalid)
                return true; // could not open to vet it → fail safe (refuse)

            if (!GetFileInformationByHandle(handle, out BY_HANDLE_FILE_INFORMATION info))
                return true; // could not read link count → fail safe (refuse)

            return info.nNumberOfLinks > 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return true; // any failure to vet → fail safe (refuse)
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);
}

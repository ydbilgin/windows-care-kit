using System.Runtime.InteropServices;
using System.Text;

namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// Thin <c>GetShortPathName</c> P/Invoke wrapper shared by the 8.3 capability probe
/// (<see cref="HostCapabilities.ShortNameSupported"/>) and the canonicalizer round-trip test, so both ask the
/// OS for a path's 8.3 short form the same way. Returns null when no short name exists (8dot3name disabled).
/// </summary>
internal static class ShortNameInterop
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

    /// <summary>The 8.3 short form of <paramref name="longPath"/>, or null if the volume has no short name for it.</summary>
    public static string? TryGetShortPathName(string longPath)
    {
        var sb = new StringBuilder(512);
        uint len = GetShortPathName(longPath, sb, (uint)sb.Capacity);
        return len == 0 || len > sb.Capacity ? null : sb.ToString();
    }
}

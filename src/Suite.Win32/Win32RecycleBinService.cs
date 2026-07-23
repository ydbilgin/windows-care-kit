using System.Runtime.InteropServices;
using WindowsCareKit.Core.Modules.Clean;

namespace WindowsCareKit.Win32;

/// <summary>
/// Read-only recycle-bin totals via <c>SHQueryRecycleBin</c> (shell32). It only queries item count and
/// size for all drives; it never empties the bin (emptying is the sanctioned <c>IRecycleBinEmptier</c>
/// in <c>Suite.Execution</c>). Querying with a null root sums every drive's bin.
/// </summary>
public sealed class Win32RecycleBinService : IRecycleBinService
{
    public RecycleBinInventory Query()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };

        int hr;
        try
        {
            hr = SHQueryRecycleBin(null, ref info);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // shell32 unavailable / marshalling fault — the totals are UNKNOWN, not zero (NEW-07).
            return RecycleBinInventory.Unavailable(ex.GetType().Name);
        }

        if (hr != 0) // S_OK is 0; any failure means the totals are UNKNOWN, not empty (was: new RecycleBinStats(0,0)).
            return RecycleBinInventory.Unavailable($"HRESULT 0x{hr:X8}");

        long bytes = info.i64Size < 0 ? 0 : info.i64Size;
        long items = info.i64NumItems < 0 ? 0 : info.i64NumItems;
        return RecycleBinInventory.Complete(new RecycleBinStats(items, bytes));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);
}

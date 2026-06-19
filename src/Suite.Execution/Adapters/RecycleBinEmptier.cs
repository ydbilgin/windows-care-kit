using System.ComponentModel;
using System.Runtime.InteropServices;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Logging;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Empties the Windows Recycle Bin via <c>SHEmptyRecycleBin</c> (shell32). This is irreversible, so the
/// Clean module must show an explicit confirm before calling it; the implementation writes the action to
/// the append-only <c>ExecutionLog</c> (start + outcome). It takes no plan because emptying the bin has
/// no per-item target signature.
/// </summary>
public sealed class RecycleBinEmptier : IRecycleBinEmptier
{
    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    private const int S_OK = 0;
    private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

    private readonly ExecutionLog _log;

    public RecycleBinEmptier(ExecutionLog log)
        => _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <inheritdoc />
    public void EmptyAll()
    {
        _log.Append("recyclebin.empty", "Emptying the Recycle Bin (all drives, irreversible)");

        // rootPath null = all drives. We KEEP the shell's own confirmation as an OS-level safety net
        // independent of the app's confirm dialog (defense in depth, spec §3) — only progress/sound suppressed.
        int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOPROGRESSUI | SHERB_NOSOUND);

        // S_OK = emptied. E_UNEXPECTED is returned when the bin is already empty — treat as success.
        if (hr is S_OK or E_UNEXPECTED)
        {
            _log.Append("recyclebin.empty.done", "Recycle Bin emptied");
            return;
        }

        _log.Append("recyclebin.empty.failed", $"SHEmptyRecycleBin failed (HRESULT 0x{hr:X8})");
        throw new Win32Exception(hr, $"SHEmptyRecycleBin failed (HRESULT 0x{hr:X8}).");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}

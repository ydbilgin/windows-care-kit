using System.Runtime.InteropServices;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Win32;

/// <summary>
/// Real <see cref="IRestorePointCreator"/>: creates a Windows System Restore point via
/// <c>SRSetRestorePointW</c> (srclient.dll) — a structured P/Invoke, never a shell/PowerShell string
/// (<c>Checkpoint-Computer</c> would launch a process, which is banned). It opens a
/// <c>BEGIN_SYSTEM_CHANGE</c> / <c>END_SYSTEM_CHANGE</c> pair around an <c>APPLICATION_UNINSTALL</c>
/// restore point and throws on failure so the sanctioned executor records it and stops the plan (fail closed).
///
/// IMPORTANT: <c>SRSetRestorePointW</c> can report SUCCESS even when System Restore is turned OFF — that would
/// be a FAKE guarantee (the user opted into a safety net that does not exist). So this creator re-checks the
/// injected <see cref="IRestorePointCapabilityProbe"/> at the START of <see cref="Create"/>, BEFORE the Win32
/// call, and THROWS (honest failure) when SR is unavailable. That closes the TOCTOU window between the UI's
/// probe and execution, and protects a non-UI caller that never consulted the probe (PR-5 audit FIX 2). When
/// the throw fires, the executor records Failed and STOPS the destructive plan — the promised rollback layer
/// isn't there, so deletion must not proceed. The action is also re-gated by the SafetyGate Allow arm.
/// </summary>
public sealed class Win32RestorePointCreator : IRestorePointCreator
{
    // dwRestorePtType
    private const int APPLICATION_UNINSTALL = 1;
    // dwEventType
    private const int BEGIN_SYSTEM_CHANGE = 100;
    private const int END_SYSTEM_CHANGE = 101;

    private readonly IRestorePointCapabilityProbe _capability;

    /// <param name="capability">
    /// The availability re-check (SR enabled on the system drive AND elevated). Required so the creator can
    /// never report a fake success against a disabled System Restore (PR-5 audit FIX 2). Injectable so the
    /// throw-when-unavailable path is host-testable with a fake.
    /// </param>
    public Win32RestorePointCreator(IRestorePointCapabilityProbe capability)
        => _capability = capability ?? throw new ArgumentNullException(nameof(capability));

    /// <inheritdoc />
    public void Create(CreateRestorePointAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Re-check capability FIRST — before any Win32 call. SRSetRestorePointW can lie (report success while
        // System Restore is OFF); failing loudly here keeps the protective guarantee honest (PR-5 audit FIX 2).
        if (!_capability.IsAvailable())
            throw new InvalidOperationException(
                "System Restore is not available (disabled on the system drive, or the process is not elevated); "
                + "refusing to report a restore point that was not created.");

        string description = Truncate(action.RestorePointName, 255);

        var begin = new RESTOREPOINTINFO
        {
            dwEventType = BEGIN_SYSTEM_CHANGE,
            dwRestorePtType = APPLICATION_UNINSTALL,
            llSequenceNumber = 0,
            szDescription = description,
        };

        if (!SRSetRestorePointW(ref begin, out STATEMGRSTATUS status))
            throw new InvalidOperationException(
                $"SRSetRestorePointW (begin) failed: status={status.nStatus}.");

        // Close the change window using the sequence number the begin call returned.
        var end = new RESTOREPOINTINFO
        {
            dwEventType = END_SYSTEM_CHANGE,
            dwRestorePtType = APPLICATION_UNINSTALL,
            llSequenceNumber = status.llSequenceNumber,
            szDescription = description,
        };

        if (!SRSetRestorePointW(ref end, out STATEMGRSTATUS endStatus))
            throw new InvalidOperationException(
                $"SRSetRestorePointW (end) failed: status={endStatus.nStatus}.");
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "Windows Care Kit" : (s.Length <= max ? s : s[..max]);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RESTOREPOINTINFO
    {
        public int dwEventType;
        public int dwRestorePtType;
        public long llSequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STATEMGRSTATUS
    {
        public int nStatus;
        public long llSequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SRSetRestorePointW(ref RESTOREPOINTINFO pRestorePtSpec, out STATEMGRSTATUS pSMgrStatus);
}

using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Execution;

/// <summary>
/// The seam the sanctioned executor dispatches a <see cref="CreateRestorePointAction"/> to. The real Win32
/// implementation lives in <c>Suite.Win32</c> (<c>SRSetRestorePointW</c> via P/Invoke); a fake implements
/// this for host tests so the Dispatch routing can be proven without touching the real machine.
///
/// Creating a restore point is the one protective system call in the suite. The executor still authorizes
/// and re-gates the action first (the gate's pure-system-call Allow arm), exactly like every other typed
/// action — there is no second execution path (UI decision §5).
/// </summary>
public interface IRestorePointCreator
{
    /// <summary>
    /// Create the System Restore point for <paramref name="action"/>. Throws on failure so the executor's
    /// try/catch records it and (because a restore point is not best-effort) stops the plan — fail closed.
    /// </summary>
    void Create(CreateRestorePointAction action);
}

namespace WindowsCareKit.Core.Execution;

/// <summary>
/// Empties the Windows Recycle Bin. This is destructive and irreversible, so it lives in the
/// sanctioned <c>Suite.Execution</c> layer (the implementation calls <c>SHEmptyRecycleBin</c>). The
/// interface is declared here in <c>Suite.Core</c> so the read-only Clean module can depend on it
/// without referencing the destructive layer directly (it is wired by DI). It takes no
/// <see cref="Planning.OperationPlan"/> because emptying the bin has no per-item target signature —
/// the caller must show an explicit confirm first and the implementation logs the action.
/// </summary>
public interface IRecycleBinEmptier
{
    /// <summary>Empties the recycle bin for all drives. Throws on a Win32 failure so the caller can report it.</summary>
    void EmptyAll();
}

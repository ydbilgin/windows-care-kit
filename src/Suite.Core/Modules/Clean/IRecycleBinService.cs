namespace WindowsCareKit.Core.Modules.Clean;

/// <summary>Read-only recycle-bin totals across all drives.</summary>
/// <param name="ItemCount">Number of items currently in the recycle bin.</param>
/// <param name="ApproxBytes">Approximate total size in bytes.</param>
public sealed record RecycleBinStats(long ItemCount, long ApproxBytes);

/// <summary>
/// Read-only query of the Windows Recycle Bin (item count + size). Emptying the bin is destructive and
/// lives in the sanctioned <c>Suite.Execution</c> layer (<c>IRecycleBinEmptier</c>), not here (spec §1.2).
/// </summary>
public interface IRecycleBinService
{
    /// <summary>Current recycle-bin totals across all drives (read-only, <c>SHQueryRecycleBin</c>).</summary>
    RecycleBinStats Query();
}

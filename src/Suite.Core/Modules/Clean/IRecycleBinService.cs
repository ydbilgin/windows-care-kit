namespace WindowsCareKit.Core.Modules.Clean;

/// <summary>Read-only recycle-bin totals across all drives.</summary>
/// <param name="ItemCount">Number of items currently in the recycle bin.</param>
/// <param name="ApproxBytes">Approximate total size in bytes.</param>
public sealed record RecycleBinStats(long ItemCount, long ApproxBytes);

/// <summary>
/// The result of a recycle-bin query: the totals when the shell query succeeded, plus source health. The bin
/// is a single all-drives query, so it is either <see cref="SourceHealth.Complete"/> (with non-null
/// <see cref="Stats"/>) or <see cref="SourceHealth.Unavailable"/> (null <see cref="Stats"/> — the totals are
/// UNKNOWN, not zero; NEW-07). <see cref="FailureCategory"/> is a safe HRESULT/type token when Unavailable.
/// </summary>
public sealed record RecycleBinInventory(RecycleBinStats? Stats, SourceHealth Health, string? FailureCategory)
{
    /// <summary>A successful query with complete totals.</summary>
    public static RecycleBinInventory Complete(RecycleBinStats stats) => new(stats, SourceHealth.Complete, null);

    /// <summary>A failed query: totals are unknown, not zero. <paramref name="safeCategory"/> must carry no path/message.</summary>
    public static RecycleBinInventory Unavailable(string safeCategory) => new(null, SourceHealth.Unavailable, safeCategory);
}

/// <summary>
/// Read-only query of the Windows Recycle Bin (item count + size). Emptying the bin is destructive and
/// lives in the sanctioned <c>Suite.Execution</c> layer (<c>IRecycleBinEmptier</c>), not here (spec §1.2).
/// </summary>
public interface IRecycleBinService
{
    /// <summary>Current recycle-bin totals across all drives, with source health (read-only, <c>SHQueryRecycleBin</c>).</summary>
    RecycleBinInventory Query();
}

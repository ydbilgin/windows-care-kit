using System;
using WindowsCareKit.Core.Modules.Clean;
using Xunit;

namespace WindowsCareKit.Tests.Clean;

public class InventoryHealthTests
{
    [Theory]
    [InlineData(0, 0, SourceHealth.Complete)]   // nothing to inspect → honest empty, not a failure
    [InlineData(3, 0, SourceHealth.Complete)]
    [InlineData(3, 3, SourceHealth.Unavailable)]
    [InlineData(1, 1, SourceHealth.Unavailable)]
    [InlineData(3, 1, SourceHealth.Partial)]
    [InlineData(6, 2, SourceHealth.Partial)]
    public void Aggregate_maps_attempted_and_failed_counts_to_health(int attempted, int failed, SourceHealth expected)
        => Assert.Equal(expected, InventoryHealth.Aggregate(attempted, failed));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 3)]   // failed > attempted is impossible
    [InlineData(2, -1)]
    public void Aggregate_rejects_impossible_counts(int attempted, int failed)
        => Assert.Throws<ArgumentOutOfRangeException>(() => InventoryHealth.Aggregate(attempted, failed));

    [Fact]
    public void RecycleBinInventory_Unavailable_carries_a_safe_category_and_null_stats()
    {
        RecycleBinInventory inv = RecycleBinInventory.Unavailable("HRESULT 0x80004005");
        Assert.Null(inv.Stats);
        Assert.Equal(SourceHealth.Unavailable, inv.Health);
        Assert.Equal("HRESULT 0x80004005", inv.FailureCategory);
    }
}

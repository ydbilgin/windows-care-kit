namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// Stable trait keys/values for xUnit <c>[Trait("Category", …)]</c> filtering.
/// Tests that touch a real destructive sink carry <c>[Trait("Category", TestCategories.Destructive)]</c>
/// so CI (and local default runs) can exclude them with <c>--filter "Category!=Destructive"</c>.
/// </summary>
internal static class TestCategories
{
    /// <summary>Tests adjacent to a real destructive operation. Excluded by default; run only on a disposable machine (Step 4).</summary>
    public const string Destructive = "Destructive";
}

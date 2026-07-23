using System.Diagnostics;
using WindowsCareKit.Core.Modules.Migration.Detection;
using Xunit;
using Xunit.Abstractions;

namespace WindowsCareKit.Tests.Migration.Detection;

/// <summary>
/// Scale fitness checks for <see cref="ProgramDedupLayer.Merge"/> (P32/P33).
/// The weak-key conflict check costs O(k * n) for a single collision cluster of size k, and O(C * n)
/// in total across the C cross-component weak-key collisions in an inventory — so many small clusters
/// can still add up to quadratic work, not just one large one. These tests pin the record-count budget
/// that shape is allowed to cost, using collision-heavy inventories.
/// <para>
/// Only the two <c>[Fact]</c> budget tests are gates. The <c>[Theory]</c> below is reporting: it prints
/// the cost curve so the quadratic shape stays visible in CI output, and asserts only that the merge
/// produced something. Do not cite it as a fitness gate.
/// </para>
/// </summary>
public sealed class ProgramDedupScaleTests(ITestOutputHelper output)
{
    /// <summary>
    /// The record count this budget is defended at: uninstall registry (machine + both views + per-user),
    /// MSI, AppX, App Paths and Start Menu shortcuts combined. A measured real machine produced ~650 raw
    /// pre-dedup rows and a heavily loaded developer machine lands in the high hundreds, so this is roughly
    /// 2x headroom. It is a <b>selected target, not an invariant</b>: no producer caps its cardinality
    /// (see ProgramDetector), so a future source could exceed it — which is exactly what these tests are
    /// meant to surface.
    /// </summary>
    private const int BudgetedRecordCount = 2000;

    /// <summary>
    /// Wall-clock budget for <see cref="BudgetedRecordCount"/> collision-heavy records, chosen with
    /// large headroom over the measured cost so a loaded or slower CI machine does not turn this into a
    /// flake. Crossing it means the merge shape — not the machine — changed.
    /// </summary>
    private const int RealisticScaleBudgetMs = 1500;

    /// <summary>
    /// Same budget for the adversarial shape: every record in one weak-key cluster with a distinct strong
    /// identity, so no union ever succeeds and the conflict check runs on every collision without
    /// short-circuiting. That is the most expensive shape at this record count, not a proven global
    /// worst case — a different key distribution could cost more per record.
    /// </summary>
    private const int AdversarialScaleBudgetMs = 3000;

    [Fact]
    public void Merge_stays_within_budget_at_realistic_scale()
    {
        DiscoveredProgram[] inventory = BuildCollisionHeavyInventory(BudgetedRecordCount, seed: 20260723);

        long elapsedMs = MeasureMerge(inventory, out int mergedCount);

        output.WriteLine($"realistic mix: {inventory.Length} records -> {mergedCount} merged in {elapsedMs} ms");
        Assert.True(
            elapsedMs <= RealisticScaleBudgetMs,
            $"Merge of {inventory.Length} collision-heavy records took {elapsedMs} ms, budget {RealisticScaleBudgetMs} ms.");
    }

    [Fact]
    public void Merge_stays_within_budget_for_worst_case_weak_key_cluster()
    {
        DiscoveredProgram[] inventory = BuildSingleWeakKeyClusterInventory(BudgetedRecordCount);

        long elapsedMs = MeasureMerge(inventory, out int mergedCount);

        output.WriteLine($"adversarial cluster: {inventory.Length} records -> {mergedCount} merged in {elapsedMs} ms");
        Assert.Equal(inventory.Length, mergedCount);
        Assert.True(
            elapsedMs <= AdversarialScaleBudgetMs,
            $"Merge of a {inventory.Length}-record weak-key cluster took {elapsedMs} ms, budget {AdversarialScaleBudgetMs} ms.");
    }

    [Theory]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(2000)]
    [InlineData(5000)]
    public void Merge_scale_curve_is_reported(int recordCount)
    {
        DiscoveredProgram[] realistic = BuildCollisionHeavyInventory(recordCount, seed: 20260723);
        DiscoveredProgram[] adversarial = BuildSingleWeakKeyClusterInventory(recordCount);

        long realisticMs = MeasureMerge(realistic, out int realisticGroups);
        long adversarialMs = MeasureMerge(adversarial, out int adversarialGroups);

        output.WriteLine(
            $"n={recordCount} | realistic {realisticMs} ms (-> {realisticGroups}) | adversarial {adversarialMs} ms (-> {adversarialGroups})");

        Assert.True(realisticGroups > 0);
        Assert.True(adversarialGroups > 0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static long MeasureMerge(DiscoveredProgram[] inventory, out int mergedCount)
    {
        // One warm-up pass so JIT and regex compilation are not charged to the measurement.
        mergedCount = ProgramDedupLayer.Merge(inventory).Count;

        var stopwatch = Stopwatch.StartNew();
        mergedCount = ProgramDedupLayer.Merge(inventory).Count;
        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }

    /// <summary>
    /// Collision-heavy inventory shaped like a real multi-source scan: most records are the same app seen
    /// by several sources (weak leaf/namepub joins that succeed), plus same-name product families whose
    /// distinct product codes force the quadratic conflict check, plus unrelated singletons.
    /// </summary>
    private static DiscoveredProgram[] BuildCollisionHeavyInventory(int count, int seed)
    {
        const int MultiSourceGroupSize = 3;
        const int SameNameFamilySize = 12;

        var random = new Random(seed);
        var items = new List<DiscoveredProgram>(count);
        ProgramSourceKind[] sources =
        [
            ProgramSourceKind.RegistryUninstall,
            ProgramSourceKind.StartMenu,
            ProgramSourceKind.AppPaths,
        ];

        int index = 0;
        while (items.Count < count)
        {
            int bucket = index % 10;
            if (bucket < 6)
            {
                // Same app discovered by several sources: shared leaf + namepub, no strong identity.
                string leaf = $"app-{index}";
                for (int i = 0; i < MultiSourceGroupSize && items.Count < count; i++)
                {
                    items.Add(Record(
                        displayName: $"App {index}",
                        publisher: "Vendor A",
                        source: sources[i % sources.Length],
                        installLeaf: leaf));
                }
            }
            else if (bucket < 9)
            {
                // Same normalized name + publisher, distinct product codes: the conflict check runs for
                // every colliding member and must refuse every union.
                for (int i = 0; i < SameNameFamilySize && items.Count < count; i++)
                {
                    items.Add(Record(
                        displayName: "Runtime Library",
                        publisher: "Vendor B",
                        source: ProgramSourceKind.RegistryUninstall,
                        installLeaf: null,
                        productCode: ProductCode(items.Count)));
                }
            }
            else
            {
                items.Add(Record(
                    displayName: $"Standalone {random.Next()}-{index}",
                    publisher: $"Vendor {index}",
                    source: ProgramSourceKind.RegistryUninstall,
                    installLeaf: $"standalone-{index}"));
            }

            index++;
        }

        return [.. items];
    }

    /// <summary>
    /// Worst case for the weak-key conflict check: one shared namepub key across every record, each with a
    /// distinct product code, so no union ever succeeds and every collision rescans the whole component.
    /// </summary>
    private static DiscoveredProgram[] BuildSingleWeakKeyClusterInventory(int count)
    {
        var items = new DiscoveredProgram[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = Record(
                displayName: "Shared Name",
                publisher: "Shared Publisher",
                source: ProgramSourceKind.RegistryUninstall,
                installLeaf: null,
                productCode: ProductCode(i));
        }

        return items;
    }

    private static string ProductCode(int ordinal)
        => $"{{{ordinal:x8}-0000-0000-0000-000000000000}}";

    private static DiscoveredProgram Record(
        string displayName,
        string publisher,
        ProgramSourceKind source,
        string? installLeaf,
        string? productCode = null)
    {
        string normalized = ProgramJoinKeys.NormalizeName(displayName);
        return new DiscoveredProgram
        {
            Id = productCode ?? installLeaf ?? $"{normalized}|{publisher.ToLowerInvariant()}",
            DisplayName = displayName,
            Publisher = publisher,
            Version = "1.0",
            InstallLocation = installLeaf is null ? null : @"C:\Program Files\" + installLeaf,
            InstallPathLeaf = installLeaf,
            ProductCode = productCode,
            NormalizedName = normalized,
            Scope = ProgramScope.Machine,
            Sources = [source],
            IsSystemComponent = false,
            ReinstallId = null,
            PackageFamilyName = null,
        };
    }
}

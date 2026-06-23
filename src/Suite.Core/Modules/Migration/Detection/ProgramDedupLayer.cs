namespace WindowsCareKit.Core.Modules.Migration.Detection;

/// <summary>
/// Field-level dedup and merge for <see cref="DiscoveredProgram"/> records from multiple sources.
/// Deterministic: output is sorted by DisplayName (ordinal-ignore-case) then Id.
/// </summary>
public static class ProgramDedupLayer
{
    /// <summary>
    /// Merges <paramref name="programs"/> by join key with field-level precedence (B.6).
    /// Dedup key priority: ProductCode → InstallPathLeaf → "NormalizedName|publisher".
    /// </summary>
    public static IReadOnlyList<DiscoveredProgram> Merge(IEnumerable<DiscoveredProgram> programs)
    {
        // Group by join key (first non-null in precedence order).
        var groups = new Dictionary<string, List<DiscoveredProgram>>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in programs)
        {
            string key = JoinKey(p);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }
            list.Add(p);
        }

        var merged = new List<DiscoveredProgram>(groups.Count);
        foreach (var group in groups.Values)
        {
            merged.Add(MergeGroup(group));
        }

        // Deterministic output order: DisplayName (ordinal-ignore-case), then Id.
        merged.Sort((a, b) =>
        {
            int c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
        });

        return merged;
    }

    // ── Private helpers ──────────────────────────────────────────────────────────────────────────

    /// <summary>Explicit scope precedence (lower = more privileged); independent of enum declaration order.</summary>
    private static int ScopeRank(ProgramScope s) => s switch
    {
        ProgramScope.Machine => 0,
        ProgramScope.CurrentUser => 1,
        ProgramScope.OtherUserNotEnumerable => 2,
        _ => 3,
    };

    private static string JoinKey(DiscoveredProgram p)
        => p.ProductCode
           ?? p.InstallPathLeaf
           ?? $"{p.NormalizedName}|{(p.Publisher ?? string.Empty).ToLowerInvariant()}";

    private static DiscoveredProgram MergeGroup(List<DiscoveredProgram> group)
    {
        if (group.Count == 1)
            return group[0];

        // Field-level merge across all records in the group.
        // DisplayName: first.
        string displayName = group[0].DisplayName;

        // Scope: most privileged (Machine > CurrentUser > OtherUserNotEnumerable). Explicit precedence
        // map (review nit: do not depend implicitly on enum declaration order).
        ProgramScope scope = group.Select(p => p.Scope).OrderBy(ScopeRank).First();

        // Sources: union, distinct, sorted by enum ordinal.
        var sources = group
            .SelectMany(p => p.Sources)
            .Distinct()
            .OrderBy(k => (int)k)
            .ToList();

        // First-non-null wins for optional fields.
        string? installLocation = group.Select(p => p.InstallLocation).FirstOrDefault(v => v != null);
        string? installPathLeaf = group.Select(p => p.InstallPathLeaf).FirstOrDefault(v => v != null);
        string? productCode     = group.Select(p => p.ProductCode).FirstOrDefault(v => v != null);
        string? version         = group.Select(p => p.Version).FirstOrDefault(v => v != null);
        string? publisher       = group.Select(p => p.Publisher).FirstOrDefault(v => v != null);

        // IsSystemComponent: false if ANY record says non-system (real-app wins).
        bool isSystem = group.All(p => p.IsSystemComponent);

        // NormalizedName: first.
        string normalizedName = group[0].NormalizedName;

        // Re-derive Id from merged fields.
        string id = productCode
            ?? installPathLeaf
            ?? $"{normalizedName}|{(publisher ?? string.Empty).ToLowerInvariant()}";

        return new DiscoveredProgram
        {
            Id              = id,
            DisplayName     = displayName,
            Publisher       = publisher,
            Version         = version,
            InstallLocation = installLocation,
            InstallPathLeaf = installPathLeaf,
            ProductCode     = productCode,
            NormalizedName  = normalizedName,
            Scope           = scope,
            Sources         = sources,
            IsSystemComponent = isSystem,
            ReinstallId     = null,
            PackageFamilyName = null,
        };
    }
}

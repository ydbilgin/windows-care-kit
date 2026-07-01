namespace WindowsCareKit.Core.Modules.Migration.Detection;

/// <summary>
/// Field-level dedup and merge for <see cref="DiscoveredProgram"/> records from multiple sources.
/// Deterministic: merge precedence uses an explicit source rank and output is sorted by DisplayName then Id.
/// </summary>
public static class ProgramDedupLayer
{
    /// <summary>
    /// Merges <paramref name="programs"/> by connected join keys with field-level precedence (B.6).
    /// Any shared namespaced key connects records into one component.
    /// </summary>
    public static IReadOnlyList<DiscoveredProgram> Merge(IEnumerable<DiscoveredProgram> programs)
    {
        ArgumentNullException.ThrowIfNull(programs);

        DiscoveredProgram[] items = programs.ToArray();
        if (items.Length == 0)
            return Array.Empty<DiscoveredProgram>();

        var uf = new UnionFind(items.Length);
        var firstByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < items.Length; i++)
        {
            foreach (string key in JoinKeys(items[i]).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (firstByKey.TryGetValue(key, out int other))
                {
                    if (CanUnionByKey(key, items, uf, i, other))
                        uf.Union(i, other);
                }
                else
                {
                    firstByKey[key] = i;
                }
            }
        }

        var groups = new Dictionary<int, List<DiscoveredProgram>>();
        for (int i = 0; i < items.Length; i++)
        {
            int root = uf.Find(i);
            if (!groups.TryGetValue(root, out List<DiscoveredProgram>? group))
                groups[root] = group = [];
            group.Add(items[i]);
        }

        var merged = groups.Values.Select(MergeGroup).ToList();

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

    /// <summary>Explicit source precedence (lower = stronger); independent of enum declaration order.</summary>
    private static int SourceRank(ProgramSourceKind source) => source switch
    {
        ProgramSourceKind.RegistryUninstall => 0,
        ProgramSourceKind.Msi => 1,
        ProgramSourceKind.Appx => 2,
        ProgramSourceKind.AppPaths => 3,
        ProgramSourceKind.StartMenu => 4,
        _ => 99,
    };

    private static IEnumerable<string> JoinKeys(DiscoveredProgram p)
    {
        if (!string.IsNullOrWhiteSpace(p.ProductCode))
            yield return "pc:" + p.ProductCode.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(p.PackageFamilyName))
            yield return "pfn:" + p.PackageFamilyName.Trim().ToLowerInvariant();

        string? reinstall = NormalizeReinstallId(p.ReinstallId);
        if (reinstall is not null)
            yield return "rid:" + reinstall;

        string? parentLeaf = ProgramJoinKeys.InstallPathParentLeaf(p.InstallLocation, p.InstallPathLeaf);
        if (parentLeaf is not null)
            yield return "leaf:" + parentLeaf;
        else if (ProgramJoinKeys.IsJoinableInstallLeaf(p.InstallPathLeaf))
            yield return "leaf:" + p.InstallPathLeaf!.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(p.NormalizedName))
            yield return $"namepub:{p.NormalizedName}|{(p.Publisher ?? string.Empty).Trim().ToLowerInvariant()}";

        // TODO(P4): add the ukn: uninstall-key tier after DiscoveredProgram grows an uninstall-key field.
    }

    private static bool CanUnionByKey(
        string key,
        DiscoveredProgram[] items,
        UnionFind uf,
        int left,
        int right)
    {
        if (IsStrongJoinKey(key))
            return true;
        if (!IsWeakJoinKey(key))
            return true;

        return !ComponentsHaveConflictingStrongIdentities(items, uf, left, right);
    }

    private static bool IsStrongJoinKey(string key)
        => key.StartsWith("pc:", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("pfn:", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("rid:", StringComparison.OrdinalIgnoreCase);

    private static bool IsWeakJoinKey(string key)
        => key.StartsWith("leaf:", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("namepub:", StringComparison.OrdinalIgnoreCase);

    private static bool ComponentsHaveConflictingStrongIdentities(
        DiscoveredProgram[] items,
        UnionFind uf,
        int left,
        int right)
    {
        int leftRoot = uf.Find(left);
        int rightRoot = uf.Find(right);
        if (leftRoot == rightRoot)
            return false;

        HashSet<string> leftProductCodes = StrongValuesForRoot(items, uf, leftRoot, p => p.ProductCode);
        HashSet<string> rightProductCodes = StrongValuesForRoot(items, uf, rightRoot, p => p.ProductCode);
        if (HaveDistinctValuesOnBothSides(leftProductCodes, rightProductCodes))
            return true;

        HashSet<string> leftPackageFamilyNames = StrongValuesForRoot(items, uf, leftRoot, p => p.PackageFamilyName);
        HashSet<string> rightPackageFamilyNames = StrongValuesForRoot(items, uf, rightRoot, p => p.PackageFamilyName);
        return HaveDistinctValuesOnBothSides(leftPackageFamilyNames, rightPackageFamilyNames);
    }

    private static HashSet<string> StrongValuesForRoot(
        DiscoveredProgram[] items,
        UnionFind uf,
        int root,
        Func<DiscoveredProgram, string?> selector)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Length; i++)
        {
            if (uf.Find(i) != root)
                continue;

            string? value = selector(items[i]);
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value.Trim().ToLowerInvariant());
        }
        return values;
    }

    private static bool HaveDistinctValuesOnBothSides(HashSet<string> left, HashSet<string> right)
        => left.Count > 0
           && right.Count > 0
           && left.Any(value => !right.Contains(value));

    private static string? NormalizeReinstallId(string? reinstallId)
    {
        if (string.IsNullOrWhiteSpace(reinstallId))
            return null;

        string value = reinstallId.Trim().ToLowerInvariant();
        return value.StartsWith("winget:", StringComparison.Ordinal)
               || value.StartsWith("choco:", StringComparison.Ordinal)
               || value.StartsWith("scoop:", StringComparison.Ordinal)
            ? value
            : "winget:" + value;
    }

    private static DiscoveredProgram MergeGroup(List<DiscoveredProgram> group)
    {
        if (group.Count == 1)
            return group[0];

        List<DiscoveredProgram> ordered = group
            .OrderBy(p => p.ProductCode is null ? 1 : 0)
            .ThenBy(MinSourceRank)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ThenBy(p => p.Publisher ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Version ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.InstallLocation ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ProductCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.PackageFamilyName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ReinstallId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.NormalizedName, StringComparer.Ordinal)
            .ToList();

        // Field-level merge across all records in the group.
        string displayName = ordered[0].DisplayName;

        // Scope: most privileged (Machine > CurrentUser > OtherUserNotEnumerable). Explicit precedence
        // map (review nit: do not depend implicitly on enum declaration order).
        ProgramScope scope = ordered.Select(p => p.Scope).OrderBy(ScopeRank).First();

        // Sources: union, distinct, sorted by enum ordinal.
        var sources = group
            .SelectMany(p => p.Sources)
            .Distinct()
            .OrderBy(SourceRank)
            .ThenBy(k => (int)k)
            .ToList();

        // First-non-null wins for optional fields.
        string? installLocation = ordered.Select(p => p.InstallLocation).FirstOrDefault(v => v != null);
        string? installPathLeaf = ordered.Select(p => p.InstallPathLeaf).FirstOrDefault(v => v != null);
        string? productCode     = ordered.Select(p => p.ProductCode).FirstOrDefault(v => v != null);
        string? packageFamilyName = ordered.Select(p => p.PackageFamilyName).FirstOrDefault(v => v != null);
        string? reinstallId     = ordered.Select(p => p.ReinstallId).FirstOrDefault(v => v != null);
        string? version         = ordered.Select(p => p.Version).FirstOrDefault(v => v != null);
        string? publisher       = ordered.Select(p => p.Publisher).FirstOrDefault(v => v != null);

        // IsSystemComponent: false if ANY record says non-system (real-app wins).
        bool isSystem = group.All(p => p.IsSystemComponent);

        // NormalizedName: first.
        string normalizedName = ordered[0].NormalizedName;

        // Re-derive Id from merged fields.
        string id = productCode
            ?? packageFamilyName
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
            ReinstallId     = reinstallId,
            PackageFamilyName = packageFamilyName,
        };
    }

    private static int MinSourceRank(DiscoveredProgram p)
        => p.Sources.Count == 0 ? int.MaxValue : p.Sources.Min(SourceRank);

    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _rank;

        public UnionFind(int count)
        {
            _parent = Enumerable.Range(0, count).ToArray();
            _rank = new int[count];
        }

        public int Find(int value)
        {
            while (_parent[value] != value)
            {
                _parent[value] = _parent[_parent[value]];
                value = _parent[value];
            }

            return value;
        }

        public void Union(int left, int right)
        {
            int rootLeft = Find(left);
            int rootRight = Find(right);
            if (rootLeft == rootRight)
                return;

            if (_rank[rootLeft] < _rank[rootRight])
            {
                _parent[rootLeft] = rootRight;
            }
            else if (_rank[rootLeft] > _rank[rootRight])
            {
                _parent[rootRight] = rootLeft;
            }
            else
            {
                _parent[rootRight] = rootLeft;
                _rank[rootLeft]++;
            }
        }
    }
}

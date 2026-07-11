namespace WindowsCareKit.Core.Modules.Migration.Selection;

/// <summary>Builds the eight ordered groups from candidates using only pure derivation services.</summary>
public static class MigrationSelectionBuilder
{
    public static IReadOnlyList<MigrationSelectionGroup> Build(
        IEnumerable<MigrationSelectionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var byCategory = MigrationCategoryClassifier.OrderedCategories
            .ToDictionary(category => category, _ => new List<MigrationSelectionItem>());
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (MigrationSelectionCandidate candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Id))
                throw new ArgumentException("candidate id is required", nameof(candidates));
            if (!ids.Add(candidate.Id))
                throw new ArgumentException($"duplicate candidate id '{candidate.Id}'", nameof(candidates));

            MigrationCategory category = MigrationCategoryClassifier.Classify(
                candidate.RecipeCategory, candidate.IsRecognized, candidate.IsAutoStub);
            MigrationBadgePresentation badge = MigrationBadgePresenter.Derive(
                candidate.Meta, candidate.RestoreTier, candidate.IsRegenerable);
            SmartDefaultDecision smartDefault = SmartDefaultScorer.Score(candidate, badge);
            byCategory[category].Add(new MigrationSelectionItem(candidate, category, badge, smartDefault));
        }

        return MigrationCategoryClassifier.OrderedCategories
            .Select(category =>
            {
                MigrationSelectionItem[] items = byCategory[category]
                    .OrderBy(item => item.Candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Candidate.Id, StringComparer.Ordinal)
                    .ToArray();
                return new MigrationSelectionGroup(category, items, GroupIntoApps(items));
            })
            .ToArray();
    }

    /// <summary>
    /// A2: group a category's items by the one canonical recipe identity used by every downstream consumer. A
    /// candidate with no recipe id (defensive only) falls back to its own id so it never merges with another app.
    /// </summary>
    private static IReadOnlyList<MigrationAppGroup> GroupIntoApps(IReadOnlyList<MigrationSelectionItem> items)
    {
        var order = new List<string>();
        var byRecipe = new Dictionary<string, List<MigrationSelectionItem>>(StringComparer.Ordinal);

        foreach (MigrationSelectionItem item in items)
        {
            string rawRecipeId = string.IsNullOrWhiteSpace(item.Candidate.Meta.RecipeId)
                ? item.Candidate.Id
                : item.Candidate.Meta.RecipeId;
            string recipeId = MigrationAppIdentity.Canonicalize(rawRecipeId);
            if (!byRecipe.TryGetValue(recipeId, out List<MigrationSelectionItem>? parts))
            {
                parts = new List<MigrationSelectionItem>();
                byRecipe[recipeId] = parts;
                order.Add(recipeId);
            }
            parts.Add(item);
        }

        return order.Select(recipeId => new MigrationAppGroup(
            recipeId,
            byRecipe[recipeId]
                .OrderBy(part => part.Candidate.Meta.ItemOrdinal)
                .ThenBy(part => part.Candidate.Id, StringComparer.Ordinal)
                .ToArray())).ToArray();
    }
}

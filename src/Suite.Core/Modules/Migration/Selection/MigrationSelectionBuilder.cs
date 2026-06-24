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
            .Select(category => new MigrationSelectionGroup(
                category,
                byCategory[category]
                    .OrderBy(item => item.Candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Candidate.Id, StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
    }
}

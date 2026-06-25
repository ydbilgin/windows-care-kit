namespace WindowsCareKit.Core.Modules.Migration.Selection;

/// <summary>The fixed M3 category order. The enum ordinal is the critical-first UI order.</summary>
public enum MigrationCategory
{
    IrreplaceablePersonal,
    SecurityIdentity,
    AiCli,
    DevConfigEditors,
    GameSaves,
    Browsers,
    ListSettingDumps,
    DetectedUnrecognized,
}

/// <summary>Selection state for a group header's three-state checkbox.</summary>
public enum GroupSelectionState
{
    None,
    Partial,
    All,
}

/// <summary>The closed copy-preview shape. <see cref="None"/> means inventory/manual-only.</summary>
public enum MigrationSourceKind
{
    None,
    File,
    Directory,
}

/// <summary>
/// Fully synthetic/presentation-safe input to the M3 selection layer. It contains no executable command and
/// no IO delegate: paths remain data until <see cref="MigrationCommandPreviewGenerator"/> quotes them into a
/// display-only string.
/// </summary>
public sealed record MigrationSelectionCandidate
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string RecipeCategory { get; init; }
    public required MigrationItemMeta Meta { get; init; }
    public required RestoreTier RestoreTier { get; init; }

    public string? SourcePath { get; init; }
    public string? DestinationPath { get; init; }
    public MigrationSourceKind SourceKind { get; init; }
    public string WhatHappens { get; init; } = string.Empty;
    public string? WhatHappensTr { get; init; }
    public string? WhatHappensEn { get; init; }
    public long? SizeBytes { get; init; }

    public bool HasCloudBackup { get; init; }
    public bool IsOnSystemDrive { get; init; }
    public bool IsUnique { get; init; }
    public bool IsRegenerable { get; init; }
    public bool IsAutoStub { get; init; }
    public bool IsRecognized { get; init; } = true;
    public bool OneDriveRedirectedSyncOff { get; init; }
    public bool HasInstallRecord { get; init; } = true;
    public RecipeInstallMethod? InstallMethod { get; init; }

    public bool BackedUpButNotRestored { get; init; }
    public bool RequiresRelogin { get; init; }
    public IReadOnlyList<string> ManualTodo { get; init; } = Array.Empty<string>();
}

/// <summary>One selectable row after badge/category/default derivation.</summary>
public sealed class MigrationSelectionItem
{
    private bool _isSelected;

    internal MigrationSelectionItem(
        MigrationSelectionCandidate candidate,
        MigrationCategory category,
        MigrationBadgePresentation badge,
        SmartDefaultDecision smartDefault)
    {
        Candidate = candidate;
        Category = category;
        Badge = badge;
        SmartDefault = smartDefault;
        _isSelected = smartDefault.Kind is SmartDefaultKind.On or SmartDefaultKind.ForcedOnCritical;
    }

    public MigrationSelectionCandidate Candidate { get; }
    public MigrationCategory Category { get; }
    public MigrationBadgePresentation Badge { get; }
    public SmartDefaultDecision SmartDefault { get; }
    public bool IsForcedSelected => SmartDefault.Kind == SmartDefaultKind.ForcedOnCritical;
    public bool IsSelected => _isSelected;

    /// <summary>Apply a user/group selection. Forced silent-data-loss rows cannot be turned off.</summary>
    public void SetSelected(bool selected)
        => _isSelected = IsForcedSelected || selected;
}

/// <summary>
/// Pure two-level selection model: group changes update every item, and item changes are immediately reflected
/// by the computed three-state header. Forced rows remain selected when a group is cleared.
/// </summary>
public sealed class MigrationSelectionGroup
{
    internal MigrationSelectionGroup(MigrationCategory category, IReadOnlyList<MigrationSelectionItem> items)
    {
        Category = category;
        Items = items;
    }

    public MigrationCategory Category { get; }
    public IReadOnlyList<MigrationSelectionItem> Items { get; }
    public int SelectedCount => Items.Count(item => item.IsSelected);

    public GroupSelectionState SelectionState
        => SelectedCount switch
        {
            0 => GroupSelectionState.None,
            var selected when selected == Items.Count && Items.Count > 0 => GroupSelectionState.All,
            _ => GroupSelectionState.Partial,
        };

    public void SetAll(bool selected)
    {
        foreach (MigrationSelectionItem item in Items)
            item.SetSelected(selected);
    }

    public void SetItem(MigrationSelectionItem item, bool selected)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Items.Contains(item))
            throw new ArgumentException("item does not belong to this group", nameof(item));
        item.SetSelected(selected);
    }
}

/// <summary>Fixed category mapping. Unknown/auto-stub rows always land in category 8.</summary>
public static class MigrationCategoryClassifier
{
    public static IReadOnlyList<MigrationCategory> OrderedCategories { get; } =
        Enum.GetValues<MigrationCategory>();

    public static MigrationCategory Classify(string? recipeCategory, bool isRecognized, bool isAutoStub)
    {
        if (!isRecognized || isAutoStub)
            return MigrationCategory.DetectedUnrecognized;

        return recipeCategory?.Trim().ToLowerInvariant() switch
        {
            "irreplaceable" or "personal" or "projects" => MigrationCategory.IrreplaceablePersonal,
            "security" or "identity" or "credentials" => MigrationCategory.SecurityIdentity,
            "ai-cli" or "ai" => MigrationCategory.AiCli,
            "dev-tools" or "dev-config" or "editors" => MigrationCategory.DevConfigEditors,
            "games" or "game-saves" => MigrationCategory.GameSaves,
            "browsers" => MigrationCategory.Browsers,
            "lists" or "setting-dumps" or "utilities" or "office" or "media" or "communication"
                => MigrationCategory.ListSettingDumps,
            _ => MigrationCategory.DetectedUnrecognized,
        };
    }
}

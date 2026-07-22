namespace WindowsCareKit.Core.Modules.Migration.Selection;

/// <summary>
/// Whether a candidate's source is known to also live in a synced cloud backup (e.g. OneDrive). This is a
/// three-state capability result, not a bare boolean: <see cref="Unknown"/> means the cloud-backup capability
/// was not determined (no signal wired, or the signal could not evaluate), and MUST NOT be treated as a
/// verified "not backed up" — collapsing unknown into absent is the exact honesty defect LSP-02 fixes. The
/// default is <see cref="Unknown"/> (ordinal 0) so a candidate that never sets it is honestly undetermined.
/// </summary>
public enum CloudBackupStatus
{
    /// <summary>Not determined — no signal was wired, or the signal could not evaluate this source.</summary>
    Unknown,

    /// <summary>A capable signal checked and the source is NOT contained in a known cloud-backup root.</summary>
    NotBackedUp,

    /// <summary>A capable signal checked and the source IS contained in a known cloud-backup root.</summary>
    BackedUp,
}

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

    /// <summary>Cloud-backup capability result for <see cref="SourcePath"/> (LSP-02: honest tri-state, default Unknown).</summary>
    public CloudBackupStatus CloudBackup { get; init; }
    public bool IsOnSystemDrive { get; init; }
    public bool IsUnique { get; init; }
    public bool IsRegenerable { get; init; }
    public bool IsAutoStub { get; init; }
    public bool IsRecognized { get; init; }
    public bool OneDriveRedirectedSyncOff { get; init; }
    public bool HasInstallRecord { get; init; }
    public RecipeInstallMethod? InstallMethod { get; init; }

    public bool BackedUpButNotRestored { get; init; }
    public bool RequiresRelogin { get; init; }
    public IReadOnlyList<string> ManualTodo { get; init; } = Array.Empty<string>();
}

/// <summary>One selectable row after badge/category/default derivation.</summary>
public sealed class MigrationSelectionItem
{
    private bool _isSelected;
    private MigrationAppGroup? _app;

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

    /// <summary>
    /// Apply selection through the owning app. Per-part interaction is not supported in v1, so this preserves the
    /// all-parts-equal invariant even for programmatic callers of the legacy item API.
    /// </summary>
    public void SetSelected(bool selected)
    {
        if (_app is not null)
        {
            _app.SetAppSelected(selected);
            return;
        }

        SetSelectedDirect(selected);
    }

    internal void AttachToApp(MigrationAppGroup app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (_app is not null && !ReferenceEquals(_app, app))
            throw new InvalidOperationException("selection item already belongs to an app");
        _app = app;
    }

    internal void SetSelectedDirect(bool selected)
        => _isSelected = IsForcedSelected || selected;
}

/// <summary>
/// Pure two-level selection model: group changes update every item, and item changes are immediately reflected
/// by the computed three-state header. Forced rows remain selected when a group is cleared.
/// </summary>
public sealed class MigrationSelectionGroup
{
    internal MigrationSelectionGroup(
        MigrationCategory category,
        IReadOnlyList<MigrationSelectionItem> items,
        IReadOnlyList<MigrationAppGroup> apps)
    {
        Category = category;
        Items = items;
        Apps = apps;
    }

    public MigrationCategory Category { get; }
    public IReadOnlyList<MigrationSelectionItem> Items { get; }

    /// <summary>A2: this category's items grouped into apps (one per recipe). Presentation-side only — the
    /// builder derives this from <see cref="Items"/>; it never touches the scanner.</summary>
    public IReadOnlyList<MigrationAppGroup> Apps { get; }

    public int SelectedCount => Items.Count(item => item.IsSelected);

    /// <summary>A6: the count the user sees is app rows, not files.</summary>
    public int AppCount => Apps.Count;

    /// <summary>A6: an app counts as selected the moment it is effectively selected (§ <see cref="MigrationAppGroup.IsEffectivelySelected"/>).</summary>
    public int SelectedAppCount => Apps.Count(app => app.IsEffectivelySelected);

    public GroupSelectionState SelectionState
        => SelectedAppCount switch
        {
            0 => GroupSelectionState.None,
            var selected when selected == Apps.Count && Apps.Count > 0 => GroupSelectionState.All,
            _ => GroupSelectionState.Partial,
        };

    public void SetAll(bool selected)
    {
        foreach (MigrationAppGroup app in Apps)
            app.SetAppSelected(selected);
    }

    public void SetItem(MigrationSelectionItem item, bool selected)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Items.Contains(item))
            throw new ArgumentException("item does not belong to this group", nameof(item));
        item.SetSelected(selected);
    }
}

/// <summary>
/// One app (A2): every <see cref="MigrationSelectionItem"/> sharing a recipe id, grouped by
/// <see cref="MigrationSelectionBuilder"/>. Fallback candidates (<c>{recipe.Id}#present</c>) and single-item
/// recipes naturally end up as single-part apps — same type, no expander shown by the view (A1/A4).
/// </summary>
public sealed class MigrationAppGroup
{
    internal MigrationAppGroup(string recipeId, IReadOnlyList<MigrationSelectionItem> parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeId);
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
            throw new ArgumentException("an app group must own at least one part", nameof(parts));

        RecipeId = recipeId;
        Parts = parts;
        foreach (MigrationSelectionItem part in Parts)
            part.AttachToApp(this);

        // The application is the v1 selection unit. Normalize initial per-part smart defaults immediately so a
        // checked row cannot hide a partial recipe that capture would nevertheless carry in full.
        SetAppSelected(Parts.Any(part => part.IsSelected));
    }

    public string RecipeId { get; }

    /// <summary>The parts this app owns, in builder order. Owned, not copied — toggling a part here is the
    /// same mutable <see cref="MigrationSelectionItem"/> the flat <see cref="MigrationSelectionGroup.Items"/> sees.</summary>
    public IReadOnlyList<MigrationSelectionItem> Parts { get; }

    /// <summary>The first part's candidate — every part of one recipe shares the same display name/description.</summary>
    public MigrationSelectionCandidate Representative => Parts[0].Candidate;

    public bool HasMultipleParts => Parts.Count > 1;
    public int SelectedCount => Parts.Count(part => part.IsSelected);
    public bool IsForced => Parts.Any(part => part.IsForcedSelected);

    /// <summary>
    /// Honesty floor (A1/A3): true the moment ANY part is selected. This mirrors what the engine actually does —
    /// <c>BuildCapturePlanAsync</c> reduces the selection to <c>Distinct(RecipeId)</c>, so a single selected part
    /// already pulls in the whole recipe. Displaying "selected" only when literally every part is selected would
    /// under-report an app the capture plan is about to carry in full.
    /// </summary>
    public bool IsEffectivelySelected => IsForced || Parts.Any(part => part.IsSelected);

    /// <summary>App-level recommended default (A3): ON if ANY part's smart default recommends selection.</summary>
    public bool SmartDefaultOn
        => Parts.Any(part => part.SmartDefault.Kind is SmartDefaultKind.On or SmartDefaultKind.ForcedOnCritical);

    /// <summary>
    /// A3: the app checkbox is the only v1 selection control. A forced app remains uniformly selected even when a
    /// group clear or legacy per-item call tries to clear it, so partial internal app state is unrepresentable.
    /// </summary>
    public void SetAppSelected(bool selected)
    {
        bool normalized = IsForced || selected;
        foreach (MigrationSelectionItem part in Parts)
            part.SetSelectedDirect(normalized);
    }
}

/// <summary>Canonical app identity shared by grouping, lookup and capture. Recipe IDs are case-insensitive at
/// the UI boundary, but represented once as an ordinal lower-case key so duplicate rows cannot collapse later.</summary>
public static class MigrationAppIdentity
{
    public static string Canonicalize(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return id.Trim().ToLowerInvariant();
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

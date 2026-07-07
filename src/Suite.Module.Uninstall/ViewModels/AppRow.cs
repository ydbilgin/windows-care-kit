using System.Windows.Media;
using WindowsCareKit.Core.Modules.Uninstall;

namespace WindowsCareKit.App.ViewModels;

/// <summary>
/// One row in the unified Sil DataGrid. It flattens the two inventory sources — classic desktop programs
/// (<see cref="InstalledApp"/>) and per-user Store apps (<see cref="InstalledAppx"/>) — into a single shape
/// so one virtualized grid can show both (UI decision §2: flat, name-sorted, no grouping in v1). Fields a
/// source lacks render as "—". This is a read-only projection: it never removes anything and never mutates
/// the backing collection (the <see cref="System.Windows.Data.ICollectionView"/> filter sees these rows).
/// </summary>
public sealed class AppRow
{
    private AppRow(string displayName)
    {
        DisplayName = displayName;
    }

    /// <summary>The classic program behind this row, or null when this is a Store app.</summary>
    public InstalledApp? App { get; private init; }

    /// <summary>The Store package behind this row, or null when this is a classic program.</summary>
    public InstalledAppx? Appx { get; private init; }

    /// <summary>True for a Windows Store (AppX) row — drives the Store status badge + "—" size/date fields.</summary>
    public bool IsStore => Appx is not null;

    /// <summary>Placeholder glyph (v1 — no real icon extraction this PR; see UI decision §2 "İkon: v1").</summary>
    public string IconGlyph => IsStore ? "" /* Store */ : "" /* generic app */;

    public string DisplayName { get; }
    public string Publisher { get; private init; } = InstalledApp.Em;
    public string Version { get; private init; } = InstalledApp.Em;

    /// <summary>Human-readable install size (KB→MB/GB) or "—". Store apps report "—" (no registry size).</summary>
    public string SizeDisplay { get; private init; } = InstalledApp.Em;

    /// <summary>Install date (yyyy-MM-dd) or "—". Store apps report "—".</summary>
    public string InstallDateDisplay { get; private init; } = InstalledApp.Em;

    /// <summary>
    /// A short, human-language status badge — empty when nothing is worth flagging. Mapped from existing
    /// metadata only (no new probing). NEVER "safe/güvenli" and never green-for-present (UI decision Hard rules,
    /// §7). The mapping order is: Store app → admin needed → broken uninstaller → "" .
    /// </summary>
    public string StatusBadge { get; private init; } = string.Empty;

    public bool HasStatusBadge => StatusBadge.Length > 0;

    /// <summary>
    /// The localized badge label (e.g. "[Yönetici]"), resolved by the VM from <see cref="StatusBadge"/> after
    /// projection so this presentation type stays free of the i18n dependency. Empty when no badge.
    /// </summary>
    public string BadgeText { get; set; } = string.Empty;

    /// <summary>
    /// Which neutral palette the badge uses. Never green for "uninstaller present" (no naked-green for health).
    /// Broken uninstaller = amber attention; admin / store = neutral gray.
    /// </summary>
    public StatusTone StatusTone { get; private init; } = StatusTone.Neutral;

    /// <summary>The badge foreground brush from <see cref="StatusTone"/> — neutral gray or amber, never green.</summary>
    public Brush BadgeBrush => StatusTone == StatusTone.Attention ? Amber : Gray;

    private static readonly Brush Gray = Frozen("#B8AD96");   // Text.Muted — neutral
    private static readonly Brush Amber = Frozen("#E6B25E");  // Gold — attention, NOT green

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>True when removing this app needs elevation (HKLM machine-wide) — drives the "[Yönetici]" hint.</summary>
    public bool NeedsAdmin { get; private init; }

    /// <summary>The lowercased name+publisher haystack used by the ICollectionView search filter.</summary>
    public string SearchKey { get; private init; } = string.Empty;

    public static AppRow FromApp(InstalledApp app)
    {
        string publisher = string.IsNullOrWhiteSpace(app.Publisher) ? InstalledApp.Em : app.Publisher!;
        string version = string.IsNullOrWhiteSpace(app.DisplayVersion) ? InstalledApp.Em : app.DisplayVersion!;
        var (badge, tone) = ClassifyApp(app);
        return new AppRow(app.DisplayName)
        {
            App = app,
            Publisher = publisher,
            Version = version,
            SizeDisplay = app.SizeDisplay,
            InstallDateDisplay = app.InstallDateDisplay,
            StatusBadge = badge,
            StatusTone = tone,
            NeedsAdmin = app.IsMachineWide,
            SearchKey = Haystack(app.DisplayName, app.Publisher),
        };
    }

    public static AppRow FromAppx(InstalledAppx appx)
    {
        string publisher = string.IsNullOrWhiteSpace(appx.PublisherDisplayName) ? InstalledApp.Em : appx.PublisherDisplayName!;
        string version = string.IsNullOrWhiteSpace(appx.Version) ? InstalledApp.Em : appx.Version!;
        return new AppRow(appx.DisplayName)
        {
            Appx = appx,
            Publisher = publisher,
            Version = version,
            // Store apps have no registry EstimatedSize/InstallDate — honest "—" (UI decision §2 data honesty).
            SizeDisplay = InstalledApp.Em,
            InstallDateDisplay = InstalledApp.Em,
            StatusBadge = StoreBadge,
            StatusTone = StatusTone.Neutral,
            NeedsAdmin = false,
            SearchKey = Haystack(appx.DisplayName, appx.PublisherDisplayName),
        };
    }

    // The badge label is i18n-resolved at the call site (the VM sets the localized text); these are stable
    // tokens the VM maps to "[Mağaza]" / "[Yönetici]" / "[Kaldırıcı Bozuk]" / "[Sistem Bileşeni]".
    public const string StoreBadge = "store";
    public const string AdminBadge = "admin";
    public const string BrokenBadge = "broken";

    /// <summary>
    /// Maps a classic app's existing metadata to a single status token (no new probing). System components are
    /// already filtered before they reach the grid, so the live cases are broken-uninstaller and admin-needed.
    /// A healthy, present uninstaller produces NO badge (never green-for-present — §7).
    /// </summary>
    private static (string Badge, StatusTone Tone) ClassifyApp(InstalledApp app)
    {
        if (!app.HasUninstaller)
            return (BrokenBadge, StatusTone.Attention); // amber attention, NOT a green "ok"
        if (app.IsMachineWide)
            return (AdminBadge, StatusTone.Neutral);     // neutral gray
        return (string.Empty, StatusTone.Neutral);       // healthy present uninstaller → no badge
    }

    private static string Haystack(string name, string? publisher)
        => (name + " " + (publisher ?? string.Empty)).ToLowerInvariant();
}

/// <summary>The neutral badge palettes. Deliberately no "reversible/green" tone for status — green stays an
/// undo/done affordance only (UI decision Hard rules, no-naked-green).</summary>
public enum StatusTone
{
    /// <summary>Quiet gray — informational (admin needed, Store app).</summary>
    Neutral,
    /// <summary>Amber — needs attention (broken uninstaller). Not danger-red, not ok-green.</summary>
    Attention,
}

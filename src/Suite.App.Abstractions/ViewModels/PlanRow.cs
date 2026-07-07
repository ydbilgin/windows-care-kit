using System.Windows.Media;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.App.ViewModels;

/// <summary>Maps a <see cref="RiskLevel"/> to a brush from the Strongbox palette.</summary>
public static class RiskVisuals
{
    private static readonly Dictionary<RiskLevel, Brush> Map = new()
    {
        [RiskLevel.Info] = Frozen("#867C67"),
        [RiskLevel.Low] = Frozen("#94BE8C"),
        [RiskLevel.Medium] = Frozen("#E6B25E"),
        [RiskLevel.High] = Frozen("#E8B36B"),
        [RiskLevel.Critical] = Frozen("#E08C8C"),
    };

    public static Brush For(RiskLevel level) => Map[level];

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>A single action shown in the dry-run preview.</summary>
public sealed class PlanRow
{
    public required string Text { get; init; }
    public required string RiskText { get; init; }
    public required Brush RiskBrush { get; init; }
    public required string Undo { get; init; }
    public string? Detail { get; init; }

    /// <summary>True when this row is an elevated command — the UI may highlight it.</summary>
    public bool IsElevated { get; init; }

    public static PlanRow FromAction(PlannedAction a)
    {
        var (text, detail, elevated) = Describe(a);
        return new PlanRow
        {
            Text = text,
            RiskText = a.Risk.ToString(),
            RiskBrush = RiskVisuals.For(a.Risk),
            Undo = "undo: " + a.Undo,
            Detail = detail,
            IsElevated = elevated,
        };
    }

    /// <summary>
    /// Same as <see cref="FromAction(PlannedAction)"/> but, when <paramref name="isWholeTree"/> is true (the
    /// copy Source is an existing directory, i.e. a recursive tree copy rather than a single file), appends a
    /// "(whole-tree copy)" warning to the detail so the dry-run preview never hides a recursive copy behind one
    /// opaque row (L7). The directory probe is done by the caller off-thread; this method stays pure.
    /// </summary>
    public static PlanRow FromAction(PlannedAction a, bool isWholeTree)
    {
        PlanRow row = FromAction(a);
        if (!isWholeTree || a is not CopyAction)
            return row;

        const string warning = "(whole-tree copy)";
        string detail = string.IsNullOrWhiteSpace(row.Detail) ? warning : row.Detail + "   ·   " + warning;
        return new PlanRow
        {
            Text = row.Text,
            RiskText = row.RiskText,
            RiskBrush = row.RiskBrush,
            Undo = row.Undo,
            Detail = detail,
            IsElevated = row.IsElevated,
        };
    }

    public static PlanRow FromSkipped(PlannedAction a, string reason)
    {
        var (text, _, _) = Describe(a);
        return new PlanRow
        {
            Text = text,
            RiskText = "BLOCKED",
            RiskBrush = RiskVisuals.For(RiskLevel.Critical),
            Undo = string.Empty,
            Detail = reason,
        };
    }

    /// <summary>
    /// Derives the preview text from the action's TYPED fields — the real path / registry key / command +
    /// arguments / copy source→destination — never the free-text Description, so the user sees exactly what
    /// will run (spec §2/§3). Elevated commands are flagged inline with <c>[ELEVATED]</c>.
    /// </summary>
    private static (string Text, string Detail, bool Elevated) Describe(PlannedAction a) => a switch
    {
        CommandAction c => (
            (c.RequiresElevation ? "[ELEVATED] " : string.Empty) + "Run: " + c.FileName,
            (c.Arguments.Count > 0 ? "args: " + string.Join(" ", c.Arguments) + "   ·   " : string.Empty) + a.Reason,
            c.RequiresElevation),
        FileDeleteAction f => ("Delete: " + f.Path, a.Reason, false),
        RegistryDeleteAction r => (
            $"Registry {(r.ValueName is null ? "key" : "value")}: {r.Hive}\\{r.SubKeyPath}"
                + (r.ValueName is null ? string.Empty : "  ::  " + r.ValueName),
            a.Reason, false),
        ServiceDeleteAction s => ($"Service {s.Operation}: {s.ServiceName}", a.Reason, false),
        TaskDeleteAction t => ($"Task {t.Operation}: {t.TaskPath}", a.Reason, false),
        CopyAction cp => ($"Copy: {cp.Source}  →  {cp.Destination}", a.Reason, false),
        RestoreMergeAction rm => ($"Restore: {rm.Source}  →  {rm.Destination}", a.Reason, false),
        _ => (a.Description, a.Reason, false),
    };
}

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Media;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
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

/// <summary>A single action shown in the dry-run preview. When constructed with an <see cref="I18n"/> it
/// re-renders its text live on a language switch (finding G6). Without one it keeps the legacy frozen-English
/// text so existing literal callers and the frozen Migration call site are unchanged. The risk-chip COLOR is
/// intentionally still the frozen <see cref="RiskVisuals"/> palette on both paths — theme-following color is
/// deferred to the visual-design track (see spec G6 scoping).</summary>
public sealed class PlanRow : ObservableObject
{
    private readonly PlannedAction? _action;
    private readonly bool _isWholeTree;
    private readonly string? _skipReason;
    private readonly I18n? _i18n;

    private readonly Brush _riskBrush = RiskVisuals.For(RiskLevel.Info);
    private readonly string _litText = string.Empty;
    private readonly string _litRiskText = string.Empty;
    private readonly string _litUndo = string.Empty;
    private readonly string? _litDetail;
    private readonly bool _litElevated;

    private readonly bool _vetoRequested;
    private bool _isIncluded;

    /// <summary>Legacy literal path: object-initializer callers set the required members directly.</summary>
    public PlanRow() { }

    [SetsRequiredMembers]
    private PlanRow(PlannedAction action, bool isWholeTree, string? skipReason, I18n i18n)
    {
        _action = action;
        _isWholeTree = isWholeTree;
        _skipReason = skipReason;
        _i18n = i18n;
        _riskBrush = RiskVisuals.For(skipReason is null ? action.Risk : RiskLevel.Critical);
        Text = string.Empty;
        RiskText = string.Empty;
        RiskBrush = _riskBrush;
        Undo = string.Empty;
        PropertyChangedEventManager.AddHandler(i18n, OnLocalizationChanged, string.Empty);
    }

    public required string Text { get => _i18n is null ? _litText : DescribeText(); init => _litText = value; }
    public required string RiskText { get => _i18n is null ? _litRiskText : RiskName(); init => _litRiskText = value; }
    public required Brush RiskBrush { get => _riskBrush; init => _riskBrush = value; }
    public required string Undo { get => _i18n is null ? _litUndo : UndoLabel(); init => _litUndo = value; }
    public string? Detail { get => _i18n is null ? _litDetail : DescribeDetail(); init => _litDetail = value; }

    /// <summary>True when this row is an elevated command — the UI may highlight it.</summary>
    public bool IsElevated
    {
        get => _i18n is null ? _litElevated : _action is CommandAction { RequiresElevation: true };
        init => _litElevated = value;
    }

    /// <summary>The action this row previews, or null on the legacy literal path (object-initializer callers,
    /// which carry rendered text only). <see cref="PlanSelection.Subset"/> can only honour a veto for a row that
    /// names its action, so a null here means the row contributes nothing to a subset.</summary>
    public PlannedAction? Action => _action;

    /// <summary>
    /// True when the engine executes at this row's granularity and the row may therefore carry a veto control.
    /// False for display-only rows: skipped rows, legacy literal rows, and any row that cannot name its
    /// <see cref="Action"/>. The view binds a checkbox if and only if this is true — a disabled checkbox still
    /// says "this is a choice", so a non-vetoable row renders a Required lock badge instead (spec §2.1 rule 2).
    /// <para>
    /// The getter, not the initializer, enforces the invariant, so it holds whatever order an object initializer
    /// assigns in: a row with no action or a skip reason can never report itself vetoable, and a caller that asks
    /// for a veto it cannot honour gets a row the view will not put a checkbox on — rather than a checkbox the
    /// engine ignores, which is the defect spec §1.1 exists to prevent.
    /// </para>
    /// </summary>
    public bool IsVetoable
    {
        get => _vetoRequested && Action is not null && !IsSkipped;
        init => _vetoRequested = value;
    }

    /// <summary>
    /// User inclusion for the veto checkbox. Meaningful only when <see cref="IsVetoable"/> is true; setting it on
    /// any other row throws, because silently accepting the write would record a choice nothing downstream can
    /// act on. Reading a non-vetoable row is safe and always returns false — <see cref="PlanSelection.Subset"/>
    /// retains such rows on the strength of <see cref="IsVetoable"/> alone, never this value.
    /// </summary>
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (!IsVetoable)
            {
                throw new InvalidOperationException(
                    "PlanRow.IsIncluded is only settable on a vetoable row. This row is display-only "
                    + $"(Action={(Action is null ? "null" : Action.Kind)}, IsSkipped={IsSkipped}), so a veto "
                    + "could not be honoured at execution time.");
            }

            SetField(ref _isIncluded, value);
        }
    }

    /// <summary>
    /// The row's TYPED recovery capability. <see cref="Undo"/> is a rendered, localized string and must never be
    /// parsed — counting recovery is arithmetic over this value only (spec §1.2), which is why this is invariant
    /// under a language switch.
    /// <para>
    /// It describes what recovering the ACTION would cost. A skipped row does not run at all, so a counter must
    /// exclude it via <see cref="IsSkipped"/> rather than read a recovery promise into it. The legacy literal
    /// path has no typed field to read, so it falls back to <see cref="UndoCapability.None"/> — the fail-safe
    /// direction, since claiming recoverability the engine never promised is the more dangerous error.
    /// </para>
    /// </summary>
    public UndoCapability Recovery => _action?.Undo ?? LegacyRecovery;

    /// <summary>True when this row was built by <see cref="FromSkipped"/> — the action will NOT run.</summary>
    public bool IsSkipped => _skipReason is not null || LegacySkipped;

    /// <summary>Typed recovery captured by the legacy factory path, which renders text but keeps no action.
    /// Defaults to <see cref="UndoCapability.None"/> so a bare object-initializer row promises nothing.</summary>
    private UndoCapability LegacyRecovery { get; init; } = UndoCapability.None;

    /// <summary>Skipped-ness for the legacy factory path, where <c>_skipReason</c> is not available.</summary>
    private bool LegacySkipped { get; init; }

    public static PlanRow FromAction(PlannedAction a, I18n? i18n = null, bool isVetoable = false)
        => FromAction(a, isWholeTree: false, i18n: i18n, isVetoable: isVetoable);

    /// <summary>
    /// Same as <see cref="FromAction(PlannedAction)"/> but, when <paramref name="isWholeTree"/> is true (the
    /// copy Source is an existing directory, i.e. a recursive tree copy rather than a single file), appends a
    /// "(whole-tree copy)" warning to the detail so the dry-run preview never hides a recursive copy behind one
    /// opaque row (L7). The directory probe is done by the caller off-thread; this method stays pure.
    /// </summary>
    public static PlanRow FromAction(PlannedAction a, bool isWholeTree, I18n? i18n = null, bool isVetoable = false)
    {
        if (i18n is null)
        {
            var (text, detail, elevated) = Describe(a);
            string finalDetail = detail;
            if (isWholeTree && a is CopyAction)
            {
                const string warning = "(whole-tree copy)";
                finalDetail = string.IsNullOrWhiteSpace(detail) ? warning : detail + "   ·   " + warning;
            }
            return new PlanRow
            {
                Text = text,
                RiskText = a.Risk.ToString(),
                RiskBrush = RiskVisuals.For(a.Risk),
                Undo = "undo: " + a.Undo,
                Detail = finalDetail,
                IsElevated = elevated,
                LegacyRecovery = a.Undo,
            };
        }

        return new PlanRow(a, isWholeTree && a is CopyAction, skipReason: null, i18n)
        {
            IsVetoable = isVetoable,
        };
    }

    /// <summary>A row for an action that will NOT run. It is display-only on both paths: it takes no
    /// <c>isVetoable</c> argument, so a skipped row is never vetoable (spec §2.1 rule 1).</summary>
    public static PlanRow FromSkipped(PlannedAction a, string reason, I18n? i18n = null)
    {
        if (i18n is null)
        {
            var (text, _, _) = Describe(a);
            return new PlanRow
            {
                Text = text,
                RiskText = "BLOCKED",
                RiskBrush = RiskVisuals.For(RiskLevel.Critical),
                Undo = string.Empty,
                Detail = reason,
                LegacyRecovery = a.Undo,
                LegacySkipped = true,
            };
        }

        return new PlanRow(a, isWholeTree: false, skipReason: reason, i18n);
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(RiskText));
        OnPropertyChanged(nameof(Undo));
        OnPropertyChanged(nameof(Detail));
    }

    private string RiskName()
        => _skipReason is null
            ? LookupOrFallback(_i18n!, $"plan.risk.{_action!.Risk}", _action!.Risk.ToString())
            : LookupOrFallback(_i18n!, "plan.blocked", "BLOCKED");

    private string UndoLabel()
    {
        if (_skipReason is not null)
            return string.Empty;

        string undo = LookupOrFallback(_i18n!, $"plan.undo.{_action!.Undo}", _action.Undo.ToString());
        return FormatOrFallback(_i18n!, "plan.undo.label", "undo: " + undo, undo);
    }

    private string DescribeText()
    {
        var (text, _, _) = DescribeLocalized(_action!, _i18n!);
        return text;
    }

    private string? DescribeDetail()
    {
        if (_skipReason is not null)
            return _skipReason;
        var (_, detail, _) = DescribeLocalized(_action!, _i18n!);
        if (_isWholeTree)
        {
            string warning = LookupOrFallback(_i18n!, "plan.wholeTree", "(whole-tree copy)");
            detail = string.IsNullOrWhiteSpace(detail) ? warning : detail + "   ·   " + warning;
        }
        return detail;
    }

    private static string LookupOrFallback(I18n i18n, string key, string fallback)
    {
        string value = i18n[key];
        return string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
    }

    private static string FormatOrFallback(I18n i18n, string key, string fallback, params object[] args)
    {
        string format = i18n[key];
        return string.Equals(format, key, StringComparison.Ordinal) ? fallback : string.Format(format, args);
    }

    /// <summary>Localized twin of <see cref="Describe"/> — same typed fields, resolved through the CURRENT I18n.</summary>
    private static (string Text, string Detail, bool Elevated) DescribeLocalized(PlannedAction a, I18n i18n) => a switch
    {
        CommandAction c => (
            FormatOrFallback(
                i18n,
                c.RequiresElevation ? "plan.verb.runElevated" : "plan.verb.run",
                (c.RequiresElevation ? "[ELEVATED] " : string.Empty) + "Run: " + c.FileName,
                c.FileName),
            (c.Arguments.Count > 0
                ? FormatOrFallback(i18n, "plan.args", "args: " + string.Join(" ", c.Arguments), string.Join(" ", c.Arguments)) + "   ·   "
                : string.Empty) + a.Reason,
            c.RequiresElevation),
        FileDeleteAction f => (FormatOrFallback(i18n, "plan.verb.delete", "Delete: " + f.Path, f.Path), a.Reason, false),
        RegistryDeleteAction r => (
            FormatOrFallback(
                i18n,
                r.ValueName is null ? "plan.verb.registryKey" : "plan.verb.registryValue",
                $"Registry {(r.ValueName is null ? "key" : "value")}: {r.Hive}\\{r.SubKeyPath}"
                    + (r.ValueName is null ? string.Empty : "  ::  " + r.ValueName),
                $"{r.Hive}\\{r.SubKeyPath}" + (r.ValueName is null ? string.Empty : "  ::  " + r.ValueName)),
            a.Reason, false),
        ServiceDeleteAction s => (FormatOrFallback(i18n, "plan.verb.service", $"Service {s.Operation}: {s.ServiceName}", s.Operation, s.ServiceName), a.Reason, false),
        TaskDeleteAction t => (FormatOrFallback(i18n, "plan.verb.task", $"Task {t.Operation}: {t.TaskPath}", t.Operation, t.TaskPath), a.Reason, false),
        CopyAction cp => (FormatOrFallback(i18n, "plan.verb.copy", $"Copy: {cp.Source}  →  {cp.Destination}", cp.Source, cp.Destination), a.Reason, false),
        RestoreMergeAction rm => (FormatOrFallback(i18n, "plan.verb.restore", $"Restore: {rm.Source}  →  {rm.Destination}", rm.Source, rm.Destination), a.Reason, false),
        _ => (a.Description, a.Reason, false),
    };

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

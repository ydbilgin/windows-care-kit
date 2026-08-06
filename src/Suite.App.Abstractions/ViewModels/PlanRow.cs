using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.App.ViewModels;

/// <summary>
/// What a <see cref="PlanRow"/> says about whether its action runs — ONE value, never a combination of
/// booleans. The pair it replaces (<c>IsVetoable</c> + <c>IsIncluded</c>, alongside <c>IsSkipped</c>) could
/// represent contradictions such as "blocked and included", and each reader had to re-derive the real meaning
/// from the combination (spec §2.0, P19-R1). Every selection question is now derived from this single value, so
/// two answers cannot disagree.
/// </summary>
public enum RowSelection
{
    /// <summary>The row names no <see cref="PlanRow.Action"/>, so it makes NO claim about execution — the
    /// legacy literal path (result rows, manual checklists, rendered text only). It is neither required nor
    /// blocked: nothing is known about the engine's relationship to it, and <see cref="PlanSelection.Subset"/>
    /// therefore ignores it entirely rather than reading a claim into it (P20-R1). This is the default, so a
    /// row that never states a selection cannot accidentally assert one.</summary>
    Unbound = 0,

    /// <summary>The engine always runs this action; the user has no choice about it (a restore point, the
    /// vendor uninstaller). The view renders the Required lock badge, never a checkbox.</summary>
    Required,

    /// <summary>The user may veto this action and currently keeps it.</summary>
    OptionalIncluded,

    /// <summary>The user may veto this action and currently vetoes it.</summary>
    OptionalExcluded,

    /// <summary>The action will NOT run — the gate's <c>WON'T RUN</c> row. Blocked DOMINATES: no other row for
    /// the same action can resurrect it (spec §2.0 rule 1).</summary>
    Blocked,
}

/// <summary>A single action shown in the dry-run preview. When constructed with an <see cref="I18n"/> it
/// re-renders its text live on a language switch (finding G6). Without one it keeps the legacy frozen-English
/// text so existing literal callers and the frozen Migration call site are unchanged. It exposes the engine's
/// typed risk and blockedness separately; the view resolves those facts through the shared chip vocabulary.</summary>
public sealed class PlanRow : ObservableObject
{
    private readonly PlannedAction? _action;
    private readonly bool _isWholeTree;
    private readonly string? _skipReason;
    private readonly I18n? _i18n;

    private readonly RiskLevel _risk = RiskLevel.Info;
    private readonly string _litText = string.Empty;
    private readonly string _litRiskText = string.Empty;
    private readonly string _litUndo = string.Empty;
    private readonly string? _litDetail;
    private readonly bool _litElevated;

    /// <summary>The row's ONE selection state; every selection question is derived from it, so no two answers
    /// can contradict each other. It is fixed at construction and afterwards moves only between the two
    /// optional states, through <see cref="IsIncluded"/>.</summary>
    private RowSelection _selection = RowSelection.Unbound;

    /// <summary>Legacy literal path: object-initializer callers set the required members directly.</summary>
    public PlanRow() { }

    [SetsRequiredMembers]
    private PlanRow(PlannedAction action, bool isWholeTree, string? skipReason, I18n i18n, bool vetoRequested)
    {
        _action = action;
        _isWholeTree = isWholeTree;
        _skipReason = skipReason;
        _i18n = i18n;

        // Decided HERE, once, from the row's own construction facts. A skipped row is Blocked whatever the
        // caller asked for, so "blocked yet vetoable" is not merely rejected downstream — it is unreachable.
        _selection = skipReason is not null
            ? RowSelection.Blocked
            : vetoRequested ? RowSelection.OptionalExcluded : RowSelection.Required;

        // Risk is NOT assigned here: its getter reads the action on this path, and its init accessor writes the
        // same _risk this line already holds — a second write of one truth, which is how the two sources drift.
        _risk = action.Risk;
        Text = string.Empty;
        RiskText = string.Empty;
        Undo = string.Empty;
        PropertyChangedEventManager.AddHandler(i18n, OnLocalizationChanged, string.Empty);
    }

    public required string Text { get => _i18n is null ? _litText : DescribeText(); init => _litText = value; }
    public required string RiskText { get => _i18n is null ? _litRiskText : RiskName(); init => _litRiskText = value; }
    /// <summary>The action's own risk. Blockedness is carried separately by <see cref="IsSkipped"/> so a
    /// skipped row keeps its recoverable engine fact while the chip's blocked family still dominates.</summary>
    public required RiskLevel Risk { get => _action?.Risk ?? _risk; init => _risk = value; }
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
    /// What this row says about whether its action runs. This is the CONTRACT; <see cref="IsVetoable"/>,
    /// <see cref="IsIncluded"/> and <see cref="IsSkipped"/> are conveniences derived from it. There is no
    /// public setter or initializer: the state comes from the construction path, so no caller can assert a
    /// selection the engine could not honour — a checkbox the engine ignores is the defect spec §1.1 exists to
    /// prevent, and the same is true of a lock badge on a row nothing will run.
    /// </summary>
    public RowSelection Selection => _selection;

    /// <summary>
    /// True when the engine executes at this row's granularity and the row may therefore carry a veto control.
    /// False for every other row: required steps, blocked (<c>WON'T RUN</c>) rows, and legacy literal rows that
    /// cannot name their <see cref="Action"/>. The view binds a checkbox if and only if this is true — a
    /// disabled checkbox still says "this is a choice", so a non-vetoable row renders a Required lock badge or
    /// its blocked reason instead (spec §2.1 rule 2).
    /// </summary>
    public bool IsVetoable => _selection is RowSelection.OptionalIncluded or RowSelection.OptionalExcluded;

    /// <summary>
    /// User inclusion for the veto checkbox — the one transition <see cref="Selection"/> allows, between
    /// <see cref="RowSelection.OptionalIncluded"/> and <see cref="RowSelection.OptionalExcluded"/>. Meaningful
    /// only when <see cref="IsVetoable"/> is true; setting it on any other row throws, because silently
    /// accepting the write would record a choice nothing downstream can act on. Reading a non-vetoable row is
    /// safe and always returns false — <see cref="PlanSelection.Subset"/> retains a required row on the strength
    /// of its <see cref="Selection"/> alone, never this value.
    /// </summary>
    public bool IsIncluded
    {
        get => _selection is RowSelection.OptionalIncluded;
        set
        {
            if (!IsVetoable)
            {
                throw new InvalidOperationException(
                    "PlanRow.IsIncluded is only settable on a vetoable row. This row is "
                    + $"{_selection} (Action={(Action is null ? "null" : Action.Kind)}), so a veto "
                    + "could not be honoured at execution time.");
            }

            RowSelection next = value ? RowSelection.OptionalIncluded : RowSelection.OptionalExcluded;
            if (_selection == next)
                return;

            _selection = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Selection));
        }
    }

    /// <summary>
    /// The row's TYPED recovery capability, or <c>null</c> when it is UNKNOWN. <see cref="Undo"/> is a rendered,
    /// localized string and must never be parsed — counting recovery is arithmetic over this value only
    /// (spec §1.2), which is why this is invariant under a language switch.
    /// <para>
    /// It describes what recovering the ACTION would cost. A skipped row does not run at all, so a counter must
    /// exclude it via <see cref="IsSkipped"/> rather than read a recovery promise into it. A bare
    /// object-initializer row carries rendered text and no typed source at all, so its capability is genuinely
    /// unknown and is reported as such: collapsing it to <see cref="UndoCapability.None"/> was fail-safe but not
    /// honest (spec §2.0b, P20-R1) — the same row's rendered text may read <c>undo: Full</c>, and a counter
    /// scored it permanent on no evidence. Callers MUST surface unknown explicitly instead of folding it into
    /// the permanent tally. (<see cref="UndoCapability"/> itself is a Core enum with no unknown member and is
    /// out of scope here, so the unknown lives in the nullability at this boundary.)
    /// </para>
    /// </summary>
    public UndoCapability? Recovery => _action?.Undo ?? LegacyRecovery;

    /// <summary>True when this row states its action will NOT run — the gate's <c>WON'T RUN</c> row, built by
    /// <see cref="FromSkipped"/> on either path.</summary>
    public bool IsSkipped => _selection is RowSelection.Blocked;

    /// <summary>Typed recovery captured by the legacy factory path, which renders text but keeps no action.
    /// Null (unknown) for a bare object-initializer row, which has no typed source to capture.</summary>
    private UndoCapability? LegacyRecovery { get; init; }

    /// <summary>Selection for the legacy factory path, where the private constructor is not used. Private, so a
    /// public object-initializer caller cannot assert a selection state; it stays <see cref="RowSelection.Unbound"/>.</summary>
    private RowSelection LegacySelection { get => _selection; init => _selection = value; }

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
                Risk = a.Risk,
                Undo = "undo: " + a.Undo,
                Detail = finalDetail,
                IsElevated = elevated,
                LegacyRecovery = a.Undo,
            };
        }

        return new PlanRow(a, isWholeTree && a is CopyAction, skipReason: null, i18n, vetoRequested: isVetoable);
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
                Risk = a.Risk,
                Undo = string.Empty,
                Detail = reason,
                LegacyRecovery = a.Undo,
                LegacySelection = RowSelection.Blocked,
            };
        }

        return new PlanRow(a, isWholeTree: false, skipReason: reason, i18n, vetoRequested: false);
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

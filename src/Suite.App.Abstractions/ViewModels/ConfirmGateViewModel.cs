using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.App.ViewModels;

/// <summary>
/// The three confirmation tiers, proportional to irreversibility (UI decision §B2). The tier decides how
/// hard the gate makes the user work before Approve unlocks — NOT whether the action is "safe" (the word
/// "safe"/"güvenli" is forbidden in any action claim; SafetyGate is a mechanism name only).
/// </summary>
public enum ConfirmTier
{
    /// <summary>Soft / reversible (recycle-bin or .reg-backed): an inline confirm card.</summary>
    Reversible = 0,

    /// <summary>Medium: a summary plus a confirm card.</summary>
    Medium = 1,

    /// <summary>Hard / IRREVERSIBLE: red banner + type-to-confirm, with Cancel default-focused.</summary>
    Irreversible = 2,
}

/// <summary>
/// The reusable confirmation gate (UI decision §B2, P0). One component replaces the four divergent ad-hoc
/// confirm UIs. It shows the honest dry-run rows (what EXACTLY will be deleted/written), picks a tier from
/// the staged plan's irreversibility, and — for the Irreversible tier — requires the user to type the
/// localized confirm word ("SİL"/"DELETE") before Approve is enabled, with Cancel as the default-focused
/// button.
///
/// The host view-model owns one of these and drives it: it sets the rows + tier when a plan is staged via
/// <see cref="Open(ConfirmTier, string, string, System.Collections.Generic.IEnumerable{PlanRow})"/>, and
/// the gate calls back into the host's existing approve/cancel handlers. The component is intentionally
/// independent of any one module so the other three modules can adopt it later (task step 5).
/// </summary>
public sealed class ConfirmGateViewModel : ObservableObject
{
    private readonly Func<Task> _onApprove;
    private readonly Action _onCancel;
    private readonly Func<bool> _isBusy;

    private bool _isOpen;
    private ConfirmTier _tier;
    private string _title = string.Empty;
    private string _body = string.Empty;
    private string _typedConfirm = string.Empty;
    private int _clippedRowCount;
    private int _clippedBlockedCount;

    /// <param name="i18n">The shared string table (live language switching).</param>
    /// <param name="onApprove">Invoked when the user approves (the host runs the staged plan here).</param>
    /// <param name="onCancel">Invoked when the user cancels (the host clears the staged plan here).</param>
    /// <param name="isBusy">True while a run is in flight — disables the gate buttons.</param>
    public ConfirmGateViewModel(I18n i18n, Action onApprove, Action onCancel, Func<bool> isBusy)
        : this(i18n, AsAsync(onApprove), onCancel, isBusy)
    {
    }

    /// <summary>
    /// Async approval overload. The gate owns the async-void ICommand boundary through
    /// <see cref="AsyncRelayCommand"/>, so post-await faults are observed and double approval is refused.
    /// </summary>
    public ConfirmGateViewModel(I18n i18n, Func<Task> onApprove, Action onCancel, Func<bool> isBusy)
    {
        I18n = i18n;
        _onApprove = onApprove ?? throw new ArgumentNullException(nameof(onApprove));
        _onCancel = onCancel;
        _isBusy = isBusy;

        ApproveCommand = new AsyncRelayCommand(_onApprove, () => CanApprove);
        CancelCommand = new RelayCommand(_onCancel, () => IsOpen && !_isBusy());

        // I18n raises "Item[]" on a language switch; refresh the computed (non-indexer) strings too.
        I18n.PropertyChanged += (_, _) => OnLanguageChanged();
    }

    private static Func<Task> AsAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return () =>
        {
            action();
            return Task.CompletedTask;
        };
    }

    public I18n I18n { get; }

    /// <summary>The honest dry-run rows shown inside the gate (reuses the shared <see cref="PlanRow"/> shape).</summary>
    public ObservableCollection<PlanRow> Rows { get; } = new();

    public ICommand ApproveCommand { get; }
    public ICommand CancelCommand { get; }

    // ---- Selection surface (SPEC §3 M3) ----

    /// <summary>
    /// The optional rows the user currently keeps. Arithmetic over <see cref="PlanRow.Selection"/> — never
    /// over a rendered string — so it is invariant under a language switch (SPEC §1.2).
    /// </summary>
    public int OptionalIncludedCount
        => Rows.Count(row => row.Selection is RowSelection.OptionalIncluded);

    /// <summary>How many rows in this gate carry a veto control at all.</summary>
    public int OptionalTotalCount => Rows.Count(row => row.IsVetoable);

    /// <summary>True when this gate offers any choice, so the counter line has something to count.</summary>
    public bool HasOptionalRows => OptionalTotalCount > 0;

    /// <summary>The "n of m optional leftovers included" line (SPEC §3 M3 gate).</summary>
    public string OptionalCounterText
        => I18n.Format("confirm.optional.counter", OptionalIncludedCount, OptionalTotalCount);

    /// <summary>
    /// The rows that will contribute an action: the required steps plus the optional ones still checked.
    /// Blocked rows are deliberately excluded — a <c>WON'T RUN</c> row states the opposite (SPEC §2.3).
    /// </summary>
    public IReadOnlyList<PlanRow> IncludedRows => Rows
        .Where(row => row.Selection is RowSelection.Required or RowSelection.OptionalIncluded)
        .ToArray();

    // ---- The clipped-content affordance (SPEC §1.3, A-M3-4) ----

    /// <summary>
    /// How many rows the scroll viewport currently hides. Only the VIEW can know this — it is a measurement
    /// of laid-out geometry, not of state — so the view reports it here and this view-model owns the
    /// localized sentence. That split keeps the string composition out of the XAML while leaving the
    /// measurement where the pixels are.
    /// </summary>
    public int ClippedRowCount => _clippedRowCount;

    /// <summary>How many of the hidden rows state that they will NOT run. Counted separately because §1.3
    /// exists for exactly that row: a <c>WON'T RUN</c> row scrolled out of sight with no cue is an honesty
    /// defect, so the affordance must name it rather than fold it into a generic total.</summary>
    public int ClippedBlockedCount => _clippedBlockedCount;

    /// <summary>True while the gate's row list has content below (or above) the fold.</summary>
    public bool HasClippedRows => _clippedRowCount > 0;

    /// <summary>The affordance's sentence. It always states the hidden count, and names the hidden
    /// <c>WON'T RUN</c> rows whenever there are any.</summary>
    public string ClippedRowsText => _clippedBlockedCount > 0
        ? I18n.Format("confirm.clipped.blocked", _clippedRowCount, _clippedBlockedCount)
        : I18n.Format("confirm.clipped.more", _clippedRowCount);

    /// <summary>
    /// Called by the view after a layout pass with what its scroll viewport is currently hiding. Silent
    /// clipping of a skipped row is prohibited (SPEC §1.3), so the counts arrive here and are re-announced
    /// as one sentence rather than being formatted at three different call sites.
    /// </summary>
    /// <param name="hiddenRows">Rows not fully inside the viewport.</param>
    /// <param name="hiddenBlockedRows">How many of those state they will not run.</param>
    public void ReportClippedRows(int hiddenRows, int hiddenBlockedRows)
    {
        if (hiddenRows < 0)
            throw new ArgumentOutOfRangeException(nameof(hiddenRows));
        if (hiddenBlockedRows < 0 || hiddenBlockedRows > hiddenRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hiddenBlockedRows),
                "The hidden blocked rows are a subset of the hidden rows; a larger count would state that the "
                + "affordance names more refusals than it hides.");
        }

        if (_clippedRowCount == hiddenRows && _clippedBlockedCount == hiddenBlockedRows)
            return;

        _clippedRowCount = hiddenRows;
        _clippedBlockedCount = hiddenBlockedRows;
        OnPropertyChanged(nameof(ClippedRowCount));
        OnPropertyChanged(nameof(ClippedBlockedCount));
        OnPropertyChanged(nameof(HasClippedRows));
        OnPropertyChanged(nameof(ClippedRowsText));
    }

    /// <summary>True when the gate is shown.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetField(ref _isOpen, value))
                RaiseDerived();
        }
    }

    public ConfirmTier Tier
    {
        get => _tier;
        private set
        {
            if (SetField(ref _tier, value))
            {
                OnPropertyChanged(nameof(IsReversibleTier));
                OnPropertyChanged(nameof(IsMediumTier));
                OnPropertyChanged(nameof(IsIrreversibleTier));
                OnPropertyChanged(nameof(BannerText));
                OnPropertyChanged(nameof(CanApprove));
            }
        }
    }

    /// <summary>The host-supplied heading for this confirmation (e.g. "Confirm — this will make changes").</summary>
    public string Title { get => _title; private set => SetField(ref _title, value); }

    /// <summary>The host-supplied honest body line (what runs / how it is backed — never "safe").</summary>
    public string Body { get => _body; private set => SetField(ref _body, value); }

    public bool IsReversibleTier => Tier == ConfirmTier.Reversible;
    public bool IsMediumTier => Tier == ConfirmTier.Medium;
    public bool IsIrreversibleTier => Tier == ConfirmTier.Irreversible;

    /// <summary>The tier banner line (Reversible/Medium honest description, or the red irreversible warning).</summary>
    public string BannerText => Tier switch
    {
        ConfirmTier.Reversible => I18n["confirm.tier.reversible"],
        ConfirmTier.Medium => I18n["confirm.tier.medium"],
        _ => I18n["confirm.tier.irreversible"],
    };

    /// <summary>The localized word the user must type to unlock Approve on the Irreversible tier ("SİL"/"DELETE").</summary>
    public string ConfirmWord => I18n["confirm.type.word"];

    /// <summary>The type-to-confirm prompt with the confirm word substituted in (e.g. <c>Type "DELETE" to confirm.</c>).</summary>
    public string TypePrompt => I18n.Format("confirm.type.prompt", ConfirmWord);

    /// <summary>What the user has typed into the type-to-confirm box (Irreversible tier only).</summary>
    public string TypedConfirm
    {
        get => _typedConfirm;
        set
        {
            if (SetField(ref _typedConfirm, value))
            {
                OnPropertyChanged(nameof(TypedMatches));
                OnPropertyChanged(nameof(CanApprove));
            }
        }
    }

    /// <summary>True once the typed text matches the confirm word (case/space-insensitive).</summary>
    public bool TypedMatches =>
        I18n.CompareInfo.Compare(TypedConfirm.Trim(), ConfirmWord.Trim(), CompareOptions.IgnoreCase) == 0;

    /// <summary>
    /// Approve is enabled only when the gate is open, no run is in flight, and — for the Irreversible tier —
    /// the user has typed the confirm word. Lower tiers allow approval as soon as the gate is open.
    /// </summary>
    public bool CanApprove => IsOpen && !_isBusy() && (Tier != ConfirmTier.Irreversible || TypedMatches);

    /// <summary>
    /// Picks the tier from the staged plan: any IRREVERSIBLE action (no undo, or Critical risk) → Irreversible;
    /// otherwise a partial-undo / Medium+ action → Medium; a wholly recycle-bin / .reg-backed plan → Reversible.
    /// Mirrors <see cref="UndoCapability"/> + <see cref="RiskLevel"/> semantics (UI decision §B2).
    ///
    /// PROTECTIVE actions (<see cref="PlannedAction.IsProtective"/>, e.g. a System Restore point) are EXEMPT
    /// from escalation: the tier is driven only by the NON-protective actions, so choosing more safety never
    /// raises the bar the user must clear (UI decision §5 / critic-fix #1). The protective action is staged as a
    /// neighbor of a destructive action, so the irreversible tier still arises from THAT destructive neighbor —
    /// it is NOT achieved by relabeling the restore point's Undo (which would break Undo-driven resolution
    /// elsewhere, UI decision §5 / critic MED#3).
    /// </summary>
    public static ConfirmTier TierFor(OperationPlan plan)
    {
        if (plan.IsEmpty)
            return ConfirmTier.Reversible;

        // Only the non-protective (destructive) actions drive the tier. A protective action never escalates.
        // SECURITY (PR-5 audit FIX 1 hardening): the exemption is keyed to the EXACT protective type via the
        // closed IsTierExempt predicate — NOT the overridable PlannedAction.IsProtective marker. So no action
        // can dodge the irreversibility ceremony, even one that (wrongly) overrides IsProtective => true.
        var driving = plan.Actions.Where(a => !IsTierExempt(a)).ToArray();
        if (driving.Length == 0)
            return ConfirmTier.Reversible;

        // Irreversible if ANY driving action can't be undone, or any is Critical risk.
        bool anyIrreversible = driving.Any(a => a.Undo == UndoCapability.None || a.Risk == RiskLevel.Critical);
        if (anyIrreversible)
            return ConfirmTier.Irreversible;

        // Medium if anything is only partially reversible, or rises to Medium+ risk.
        bool anyMedium = driving.Any(a => a.Undo == UndoCapability.Partial || a.Risk >= RiskLevel.Medium);
        return anyMedium ? ConfirmTier.Medium : ConfirmTier.Reversible;
    }

    /// <summary>
    /// The CLOSED set of tier-exempt PROTECTIVE action types — the SECURITY source of truth for the escalation
    /// exemption. Keyed to the exact sealed type (not the overridable <see cref="PlannedAction.IsProtective"/>
    /// marker), so a destructive action can never self-exempt from the Irreversible/type-to-confirm tier, even
    /// if it wrongly overrides IsProtective. Adding a future protective type is a deliberate, reviewable change
    /// HERE. (<see cref="PlannedAction.IsProtective"/> stays a semantic marker, kept in sync by a structural
    /// test, but TierFor does NOT trust it for the security decision.)
    /// </summary>
    private static bool IsTierExempt(PlannedAction a) => a is CreateRestorePointAction;

    /// <summary>Opens the gate for a staged plan, supplying the chosen tier, heading, honest body, and rows.</summary>
    public void Open(ConfirmTier tier, string title, string body, IEnumerable<PlanRow> rows)
    {
        ReplaceRows(rows);

        Tier = tier;
        Title = title;
        Body = body;
        TypedConfirm = string.Empty;
        IsOpen = true;
    }

    /// <summary>Closes the gate and resets the type-to-confirm state.</summary>
    public void Close()
    {
        ReplaceRows(Array.Empty<PlanRow>());
        TypedConfirm = string.Empty;
        IsOpen = false;
    }

    /// <summary>
    /// Swaps the gate's row set and re-subscribes to it. The subscription is what makes the counter live: a
    /// veto toggled inside the gate must move the "n of m" line, and a gate that only read its rows at Open
    /// would show the count the user started with while approving a different one.
    /// </summary>
    private void ReplaceRows(IEnumerable<PlanRow> rows)
    {
        foreach (PlanRow row in Rows)
            row.PropertyChanged -= OnRowChanged;

        Rows.Clear();
        foreach (PlanRow row in rows)
        {
            Rows.Add(row);
            row.PropertyChanged += OnRowChanged;
        }

        // A new row set invalidates the previous measurement; the view re-reports after its next layout.
        ReportClippedRows(0, 0);
        RaiseSelectionCounts();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlanRow.Selection) or nameof(PlanRow.IsIncluded))
            RaiseSelectionCounts();
    }

    private void RaiseSelectionCounts()
    {
        OnPropertyChanged(nameof(OptionalIncludedCount));
        OnPropertyChanged(nameof(OptionalTotalCount));
        OnPropertyChanged(nameof(HasOptionalRows));
        OnPropertyChanged(nameof(OptionalCounterText));
        OnPropertyChanged(nameof(IncludedRows));
    }

    /// <summary>Re-raises busy-dependent state (call when the host's IsBusy flips).</summary>
    public void RefreshBusy() => RaiseDerived();

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(ConfirmWord));
        OnPropertyChanged(nameof(TypePrompt));
        OnPropertyChanged(nameof(TypedMatches));
        OnPropertyChanged(nameof(CanApprove));
        OnPropertyChanged(nameof(ClippedRowsText));

        // The counts are re-announced even though a language switch cannot move one. That is the point: a
        // counter that parsed the localized row text would go wrong in the view-model while the gate still
        // displayed the value cached from before the switch (the M2 finding, carried into M3).
        RaiseSelectionCounts();
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(CanApprove));
        OnPropertyChanged(nameof(BannerText));
    }
}

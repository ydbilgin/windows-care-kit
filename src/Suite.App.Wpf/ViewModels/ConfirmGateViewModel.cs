using System.Collections.ObjectModel;
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
    private readonly Action _onApprove;
    private readonly Action _onCancel;
    private readonly Func<bool> _isBusy;

    private bool _isOpen;
    private ConfirmTier _tier;
    private string _title = string.Empty;
    private string _body = string.Empty;
    private string _typedConfirm = string.Empty;

    /// <param name="i18n">The shared string table (live language switching).</param>
    /// <param name="onApprove">Invoked when the user approves (the host runs the staged plan here).</param>
    /// <param name="onCancel">Invoked when the user cancels (the host clears the staged plan here).</param>
    /// <param name="isBusy">True while a run is in flight — disables the gate buttons.</param>
    public ConfirmGateViewModel(I18n i18n, Action onApprove, Action onCancel, Func<bool> isBusy)
    {
        I18n = i18n;
        _onApprove = onApprove;
        _onCancel = onCancel;
        _isBusy = isBusy;

        ApproveCommand = new RelayCommand(_onApprove, () => CanApprove);
        CancelCommand = new RelayCommand(_onCancel, () => IsOpen && !_isBusy());

        // I18n raises "Item[]" on a language switch; refresh the computed (non-indexer) strings too.
        I18n.PropertyChanged += (_, _) => OnLanguageChanged();
    }

    public I18n I18n { get; }

    /// <summary>The honest dry-run rows shown inside the gate (reuses the shared <see cref="PlanRow"/> shape).</summary>
    public ObservableCollection<PlanRow> Rows { get; } = new();

    public ICommand ApproveCommand { get; }
    public ICommand CancelCommand { get; }

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
        string.Equals(TypedConfirm.Trim(), ConfirmWord.Trim(), StringComparison.CurrentCultureIgnoreCase);

    /// <summary>
    /// Approve is enabled only when the gate is open, no run is in flight, and — for the Irreversible tier —
    /// the user has typed the confirm word. Lower tiers allow approval as soon as the gate is open.
    /// </summary>
    public bool CanApprove => IsOpen && !_isBusy() && (Tier != ConfirmTier.Irreversible || TypedMatches);

    /// <summary>
    /// Picks the tier from the staged plan: any IRREVERSIBLE action (no undo, or Critical risk) → Irreversible;
    /// otherwise a partial-undo / Medium+ action → Medium; a wholly recycle-bin / .reg-backed plan → Reversible.
    /// Mirrors <see cref="UndoCapability"/> + <see cref="RiskLevel"/> semantics (UI decision §B2).
    /// </summary>
    public static ConfirmTier TierFor(OperationPlan plan)
    {
        if (plan.IsEmpty)
            return ConfirmTier.Reversible;

        // Irreversible if ANY action can't be undone, or any action is Critical risk.
        bool anyIrreversible = plan.Actions.Any(a => a.Undo == UndoCapability.None || a.Risk == RiskLevel.Critical);
        if (anyIrreversible)
            return ConfirmTier.Irreversible;

        // Medium if anything is only partially reversible, or rises to Medium+ risk.
        bool anyMedium = plan.Actions.Any(a => a.Undo == UndoCapability.Partial || a.Risk >= RiskLevel.Medium);
        return anyMedium ? ConfirmTier.Medium : ConfirmTier.Reversible;
    }

    /// <summary>Opens the gate for a staged plan, supplying the chosen tier, heading, honest body, and rows.</summary>
    public void Open(ConfirmTier tier, string title, string body, IEnumerable<PlanRow> rows)
    {
        Rows.Clear();
        foreach (var r in rows)
            Rows.Add(r);

        Tier = tier;
        Title = title;
        Body = body;
        TypedConfirm = string.Empty;
        IsOpen = true;
    }

    /// <summary>Closes the gate and resets the type-to-confirm state.</summary>
    public void Close()
    {
        Rows.Clear();
        TypedConfirm = string.Empty;
        IsOpen = false;
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
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(CanApprove));
        OnPropertyChanged(nameof(BannerText));
    }
}

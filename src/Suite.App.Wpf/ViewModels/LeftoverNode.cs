using System.Windows;
using System.Windows.Media;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.App.ViewModels;

/// <summary>
/// A checkable presentation node over one read-only <see cref="LeftoverCandidate"/> for the wizard's 3-tier
/// registry TreeView / Files list (PR-4, UI decision §6). It carries the user's per-row check state and the
/// tier visuals; it NEVER deletes anything. The deletion plan is rebuilt by <see cref="LeftoverPlanBuilder"/>
/// from the SELECTED candidates (see <see cref="ToCandidate"/>), then flows through the existing
/// stage → ComputeHash → ConfirmGate → GatedExecutor pipeline unchanged.
///
/// The 3-tier mapping (the load-bearing rule) lives in <see cref="CanCheck"/>:
/// <list type="bullet">
/// <item><b>ProgramOwned</b> — <see cref="CanCheck"/> = true (bold, checkable, DEFAULT UNCHECKED).</item>
/// <item><b>Shared</b> — <see cref="CanCheck"/> = false (normal weight, checkbox DISABLED, shown for context).</item>
/// <item><b>Protected</b> — <see cref="CanCheck"/> = false and <see cref="HasCheckbox"/> = false (teal + shield,
/// no checkbox at all; the gate's reason is rendered as a "SafetyGate korudu" line).</item>
/// </list>
/// A Shared or Protected node can never be checked, so it can never contribute to the built plan.
/// </summary>
public sealed class LeftoverNode : ObservableObject
{
    private readonly LeftoverCandidate _candidate;
    private bool _isChecked;

    public LeftoverNode(LeftoverCandidate candidate, string targetText, string subText)
    {
        _candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        TargetText = targetText;
        SubText = subText;
    }

    /// <summary>The underlying classification — the single source of truth for the 3-tier behavior.</summary>
    public LeftoverClassification Classification => _candidate.Classification;

    public bool IsProgramOwned => Classification == LeftoverClassification.ProgramOwned;
    public bool IsShared => Classification == LeftoverClassification.Shared;
    public bool IsProtected => Classification == LeftoverClassification.Protected;

    /// <summary>The registry-key / file-path text (rendered bold for ProgramOwned, teal for Protected).</summary>
    public string TargetText { get; }

    /// <summary>The attribution / gate-reason subline ("…tarafından oluşturuldu" / "SafetyGate korudu …").</summary>
    public string SubText { get; }

    /// <summary>
    /// Whether this node's checkbox is interactive. ONLY ProgramOwned is checkable (spec §6); Shared shows a
    /// disabled checkbox for context, Protected shows none. This is the 3-tier CanCheck mapping the tests pin.
    /// </summary>
    public bool CanCheck => IsProgramOwned;

    /// <summary>Shared/ProgramOwned render a checkbox (Shared's is disabled); Protected renders none.</summary>
    public bool HasCheckbox => !IsProtected;

    /// <summary>
    /// The user's per-row choice. Setting it on a non-checkable node is a no-op — Shared/Protected can never
    /// become selected, so they can never reach <see cref="ToCandidate"/>'s Selected=true plan contribution.
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!CanCheck)
                return; // a Shared/Protected row is never selectable (defense in depth alongside the builder)
            SetField(ref _isChecked, value);
        }
    }

    /// <summary>The tier brush: ProgramOwned = text, Shared = muted, Protected = teal.</summary>
    public Brush TargetBrush => IsProtected ? Teal : IsShared ? Muted : Text;

    /// <summary>ProgramOwned rows are bold; everything else is normal weight.</summary>
    public FontWeight TargetWeight => IsProgramOwned ? FontWeights.Bold : FontWeights.Normal;

    /// <summary>
    /// Project this node back to a <see cref="LeftoverCandidate"/> carrying the user's current
    /// <see cref="LeftoverCandidate.Selected"/> choice, so the plan builder can rebuild the deletion plan.
    /// A non-checkable node always projects Selected = false (it can never be checked).
    /// </summary>
    public LeftoverCandidate ToCandidate() => _candidate with { Selected = CanCheck && IsChecked };

    private static readonly Brush Text = Frozen("#F4EEE0");
    private static readonly Brush Muted = Frozen("#B8AD96");
    private static readonly Brush Teal = Frozen("#7FC2A8");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>
/// Ownership tier of a leftover candidate. Drives the 3-tier wizard TreeView (PR-4) and — critically —
/// the plan-builder invariant: ONLY <see cref="ProgramOwned"/> may ever be deleted.
/// </summary>
public enum LeftoverClassification
{
    /// <summary>
    /// Conservatively attributed to THIS app alone: its own Uninstall entry, the EXACT
    /// <c>Software\&lt;Publisher&gt;\&lt;DisplayName&gt;</c> leaf, or a service whose image path lives under the
    /// install directory. Selectable; the only tier that can enter the deletion plan.
    /// </summary>
    ProgramOwned,

    /// <summary>
    /// The DEFAULT for everything not provably owned: vendor parent keys, file associations (HKCR/Classes),
    /// <c>Installer\Products</c> refs, App Paths, Run/RunOnce, and any widened probe source. Removing one of
    /// these could affect other software, so it is display-only — never selectable, never deletable. NOTE:
    /// the authoritative <see cref="WindowsCareKit.Core.Safety.SafetyGate"/> does NOT block Shared; the
    /// plan-builder invariant (and PR-4's disabled checkbox) is what keeps Shared out of the plan.
    /// </summary>
    Shared,

    /// <summary>
    /// Anything the <see cref="WindowsCareKit.Core.Safety.SafetyGate"/> refused (system directory, critical
    /// service, protected registry subtree, …). Display-only; the gate re-blocks it at stage and at run.
    /// </summary>
    Protected,
}

/// <summary>
/// A read-only presentation projection of one leftover, pairing the underlying typed
/// <see cref="PlannedAction"/> with its ownership <see cref="Classification"/>. Pure data — it carries no
/// behavior beyond projecting the gate/classifier decision. The deletion plan is rebuilt from the SELECTED
/// candidates (see <see cref="LeftoverPlanBuilder"/>); a candidate is never executed directly.
/// </summary>
public sealed record LeftoverCandidate
{
    public required PlannedAction Action { get; init; }

    public required LeftoverClassification Classification { get; init; }

    /// <summary>
    /// True only for <see cref="LeftoverClassification.ProgramOwned"/>. Shared and Protected are
    /// display-only, so their checkbox is disabled in the wizard (PR-4) and the plan builder rejects them.
    /// </summary>
    public bool Selectable => Classification == LeftoverClassification.ProgramOwned;

    /// <summary>
    /// The user's current choice. Defaults to FALSE even for ProgramOwned — the user opts IN to each
    /// deletion (spec §6: "varsayılan İŞARETSİZ"). Setting Selected on a non-selectable candidate is
    /// meaningless; the plan builder enforces selectability regardless of this flag.
    /// </summary>
    public bool Selected { get; init; }

    /// <summary>Why this tier was assigned — the gate's reason for Protected, the attribution note otherwise.</summary>
    public required string GateReason { get; init; }
}

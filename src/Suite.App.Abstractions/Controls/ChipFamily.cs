using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.App.Controls;

/// <summary>
/// The four visual families of the chip vocabulary (binding decision §2.1). Colour carries the FAMILY; the
/// chip's text carries the exact grade word. A family is never inferred from a colour literal at a call site —
/// every chip asks for a family by name and the shared <see cref="FamilyChip"/> resolves it to theme tokens.
/// </summary>
public enum ChipFamily
{
    /// <summary>Machine state and inventory facts. Never a judgement about safety.</summary>
    Neutral = 0,

    /// <summary>Emerald. States a CAPABILITY — this is reversible, or this succeeded and was verified.
    /// Never captions "safe" or "healthy" (decision §2.4, emerald scarcity).</summary>
    Reversible = 1,

    /// <summary>Amber. Attention: partial data, a degraded read, a step that needs a decision.
    /// Explicitly NOT irreversibility.</summary>
    Attention = 2,

    /// <summary>Red. Reserved for irreversible / blocked / failed, and nothing else (decision §2.5-12).</summary>
    Irreversible = 3,
}

/// <summary>
/// The single authoritative map from engine risk to chip family (SPEC §3 M1). It lives in C# rather than in a
/// XAML trigger so the vocabulary can be asserted without rendering, and so there is exactly one place to
/// change if the vocabulary ever moves — a second mapping anywhere else is a defect, not an optimisation.
/// </summary>
public static class RiskChipFamilies
{
    /// <summary>
    /// Resolves the family for a plan row.
    /// <para>
    /// <paramref name="isSkipped"/> wins over the risk level: a row the engine will NOT run is BLOCKED, and
    /// blocked belongs to the red family whatever the underlying action's risk was. Reading the risk level of
    /// a skipped row would paint the most safety-critical row in the calm colour of the work it is refusing.
    /// </para>
    /// </summary>
    public static ChipFamily For(RiskLevel risk, bool isSkipped) => isSkipped
        ? ChipFamily.Irreversible
        : risk switch
        {
            RiskLevel.Info => ChipFamily.Neutral,
            RiskLevel.Low => ChipFamily.Reversible,
            RiskLevel.Medium => ChipFamily.Attention,
            RiskLevel.High => ChipFamily.Attention,
            RiskLevel.Critical => ChipFamily.Irreversible,
            _ => ChipFamily.Neutral,
        };

    /// <summary>
    /// Resolves the family for a per-row recovery statement. <see cref="UndoCapability.None"/> is deliberately
    /// NOT the red family: decision §2.1 requires "undo: none — permanent" to render in muted ink rather than
    /// shouting, because the row's own RiskChip already carries the irreversibility. Muting is the caller's
    /// job via <see cref="ChipFamily.Neutral"/>.
    /// </summary>
    public static ChipFamily For(UndoCapability recovery) => recovery switch
    {
        UndoCapability.Full => ChipFamily.Reversible,
        UndoCapability.Partial => ChipFamily.Attention,
        UndoCapability.None => ChipFamily.Neutral,
        _ => ChipFamily.Neutral,
    };
}

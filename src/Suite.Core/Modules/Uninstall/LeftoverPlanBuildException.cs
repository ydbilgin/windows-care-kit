using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>
/// Thrown by <see cref="LeftoverPlanBuilder"/> when a non-<see cref="LeftoverClassification.ProgramOwned"/>
/// action would enter the deletion plan. This is the load-bearing guard of spec §6 ("Shared asla planda
/// olmaz"): it fires BEFORE the plan is hashed or authorized, so a Shared or Protected action can never
/// reach the <c>GatedExecutor</c>. It signals a programming error (a caller that built a candidate with the
/// wrong tier or bypassed selectability), not a recoverable runtime condition.
/// </summary>
public sealed class LeftoverPlanBuildException : InvalidOperationException
{
    public LeftoverClassification Classification { get; }
    public string ActionKind { get; }
    public string ActionTarget { get; }

    public LeftoverPlanBuildException(PlannedAction action, LeftoverClassification classification)
        : base($"A {classification} action ({action.Kind}: {action.TargetSignature()}) cannot enter the leftover deletion plan — only ProgramOwned actions are deletable.")
    {
        Classification = classification;
        ActionKind = action.Kind;
        ActionTarget = action.TargetSignature();
    }
}

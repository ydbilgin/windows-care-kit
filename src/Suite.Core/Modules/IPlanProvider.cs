using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Modules;

/// <summary>
/// Produces a dry-run <see cref="OperationPlan"/> for a request. Building a plan is always
/// side-effect free; the plan is what the user reviews before anything runs (spec §3).
/// </summary>
/// <typeparam name="TRequest">The module-specific input (e.g. the apps the user selected to remove).</typeparam>
public interface IPlanProvider<in TRequest>
{
    OperationPlan BuildPlan(TRequest request);
}

using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Execution;

/// <summary>The result of attempting to execute a plan.</summary>
/// <param name="Ran">True only when the plan was authorized and executed.</param>
/// <param name="Reason">Why it ran or was refused.</param>
public sealed record ExecutionOutcome(bool Ran, string Reason);

/// <summary>
/// Executes a plan's typed actions — and ONLY a plan that <see cref="ExecutionAuthorizer"/> has
/// authorized (gate-clean + matching approved hash). Implementations live in a sanctioned layer that
/// is the single place allowed to touch destructive OS APIs.
///
/// The production implementation is <c>GatedExecutor</c> in the sanctioned Suite.Execution layer — the single
/// place allowed to touch destructive OS APIs. Keeping the contract here lets Core policy express and test the
/// typed-action execution seam without depending on that layer.
/// </summary>
public interface IExecutor
{
    ExecutionOutcome Execute(OperationPlan plan, string approvedPlanHash);
}

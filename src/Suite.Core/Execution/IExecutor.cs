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
/// No implementation ships in PR1: this build is read-only (inventory + dry-run). The interface exists
/// so the typed-action contract is expressed in code and so later PRs have one, testable entry point
/// for execution rather than scattered destructive calls (spec §2, §8.5).
/// </summary>
public interface IExecutor
{
    ExecutionOutcome Execute(OperationPlan plan, string approvedPlanHash);
}

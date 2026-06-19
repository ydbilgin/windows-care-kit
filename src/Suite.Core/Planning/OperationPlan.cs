using System.Security.Cryptography;
using System.Text;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Planning;

/// <summary>
/// The output of every module's dry-run: an ordered, typed, risk-classified set of actions.
/// Nothing destructive happens until the user approves this plan, and the <c>SafetyGate</c>
/// re-validates it again at execution time using <see cref="ComputeHash"/> (TOCTOU, spec §3).
/// </summary>
public sealed class OperationPlan
{
    public string Id { get; }
    public string Title { get; }
    public string ModuleName { get; }
    public IReadOnlyList<PlannedAction> Actions { get; }
    public DateTime CreatedAtUtc { get; }

    public OperationPlan(string title, string moduleName, IEnumerable<PlannedAction> actions, DateTime createdAtUtc)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        ModuleName = moduleName ?? throw new ArgumentNullException(nameof(moduleName));
        Actions = (actions ?? throw new ArgumentNullException(nameof(actions))).ToArray();
        CreatedAtUtc = createdAtUtc;
        Id = Guid.NewGuid().ToString("N");
    }

    /// <summary>The highest risk among the actions (drives the overall plan badge in the UI).</summary>
    public RiskLevel MaxRisk => Actions.Count == 0 ? RiskLevel.Info : Actions.Max(a => a.Risk);

    public bool IsEmpty => Actions.Count == 0;

    /// <summary>
    /// A deterministic SHA-256 over the ordered action target signatures (kind, target, risk, undo).
    /// Independent of the random plan/action ids and the timestamp, so the same logical plan always
    /// hashes the same — the basis for TOCTOU re-validation between preview and execution.
    /// </summary>
    public string ComputeHash()
    {
        var sb = new StringBuilder();
        foreach (var a in Actions)
        {
            sb.Append(a.TargetSignature())
              .Append("|risk=").Append((int)a.Risk)
              .Append("|undo=").Append((int)a.Undo)
              .Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

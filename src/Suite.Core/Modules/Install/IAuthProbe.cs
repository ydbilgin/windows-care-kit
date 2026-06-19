namespace WindowsCareKit.Core.Modules.Install;

/// <summary>The outcome of an auth presence probe: whether a sign-in artifact exists, by key.</summary>
/// <param name="AuthKey">Short identifier of the sign-in (e.g. <c>claude</c>).</param>
/// <param name="Present">True when the probed path exists. The contents are NEVER read (spec §1.4).</param>
/// <param name="ProbedPath">The expanded path that was checked, for display.</param>
public sealed record AuthProbeResult(string AuthKey, bool Present, string ProbedPath);

/// <summary>
/// Detects whether a CLI sign-in already exists, using <c>Test-Path</c> semantics ONLY — it checks for
/// the existence of a file/directory and never reads its contents (spec §1.4: no token/secret reads).
/// Emits no <see cref="Planning.PlannedAction"/>; it is purely informational for the UI.
/// </summary>
public interface IAuthProbe
{
    /// <summary>True when the path exists (file or directory). Contents are never opened.</summary>
    bool Exists(string path);
}

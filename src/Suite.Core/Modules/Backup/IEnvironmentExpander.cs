namespace WindowsCareKit.Core.Modules.Backup;

/// <summary>
/// Expands <c>%ENV%</c> tokens (and known-folder redirections such as OneDrive-redirected Desktop/Documents)
/// in a manifest <c>source</c> path. Injected so the planner is testable without touching the real
/// environment (spec §1.3 notes: resolve Known Folders incl. OneDrive redirection).
/// </summary>
public interface IEnvironmentExpander
{
    /// <summary>Expand environment tokens in <paramref name="path"/>; returns the original on an unknown token.</summary>
    string Expand(string path);
}

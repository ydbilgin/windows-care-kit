using WindowsCareKit.Core.Modules.Install;

namespace WindowsCareKit.Win32;

/// <summary>
/// <see cref="IAuthProbe"/> using <c>Test-Path</c> semantics ONLY: it checks whether a sign-in artifact
/// (e.g. <c>%USERPROFILE%\.claude.json</c>) exists, and NEVER opens or reads its contents (spec §1.4 —
/// no token/secret reads). Read-only; emits no action.
/// </summary>
public sealed class Win32AuthProbe : IAuthProbe
{
    /// <inheritdoc />
    public bool Exists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string expanded;
        try
        {
            expanded = Environment.ExpandEnvironmentVariables(path);
        }
        catch (Exception)
        {
            return false;
        }

        // Existence only — no FileStream, no ReadAllText. Both file and directory count as "present".
        return File.Exists(expanded) || Directory.Exists(expanded);
    }
}

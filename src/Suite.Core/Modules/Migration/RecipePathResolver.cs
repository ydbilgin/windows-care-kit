using System.IO;

namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>The three profile roots a recipe may anchor to, resolved for the CURRENT machine/user.</summary>
/// <param name="UserProfile">Absolute path of <c>%USERPROFILE%</c>.</param>
/// <param name="AppData">Absolute path of <c>%APPDATA%</c>.</param>
/// <param name="LocalAppData">Absolute path of <c>%LOCALAPPDATA%</c>.</param>
public sealed record ProfileRoots(
    string UserProfile,
    string AppData,
    string LocalAppData,
    string? ProgramData = null,
    string? ProgramFiles = null,
    string? ProgramFilesX86 = null,
    string? WindowsEtc = null)
{
    /// <summary>The roots for the running process's real user (production wiring).</summary>
    public static ProfileRoots ForCurrentUser() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers", "etc"));
}

/// <summary>Thrown when a recipe path cannot be safely resolved (fail-closed, critic fix F1/F2).</summary>
public sealed class RecipePathException : Exception
{
    public RecipePathException(string message) : base(message) { }
}

/// <summary>
/// Expands a recipe-relative path to an absolute path using ONLY the closed <see cref="KnownFolder"/> enum
/// (critic fix F1). It deliberately does NOT use the production <c>Win32EnvironmentExpander</c>, which has no
/// whitelist and would honor arbitrary <c>%ENV%</c> tokens. The rules are intentionally narrow:
/// <list type="bullet">
/// <item>the relative segment must be RELATIVE — a rooted/drive-qualified/UNC/device path is rejected;</item>
/// <item>it may not contain a <c>..</c> traversal segment or an embedded <c>%ENV%</c> token;</item>
/// <item>the combined result must stay inside the chosen profile root (lexical containment pre-check).</item>
/// </list>
/// This is the lexical first line; <see cref="RecipeResolver"/> adds the on-disk canonicalize/reparse sandbox.
/// </summary>
public sealed class RecipePathResolver
{
    private readonly ProfileRoots _roots;

    public RecipePathResolver(ProfileRoots roots)
        => _roots = roots ?? throw new ArgumentNullException(nameof(roots));

    /// <summary>The absolute, normalized root directory for a <see cref="KnownFolder"/>.</summary>
    public string RootFor(KnownFolder folder)
    {
        string raw = folder switch
        {
            KnownFolder.UserProfile => _roots.UserProfile,
            KnownFolder.AppData => _roots.AppData,
            KnownFolder.LocalAppData => _roots.LocalAppData,
            KnownFolder.ProgramData => _roots.ProgramData ?? string.Empty,
            KnownFolder.ProgramFiles => _roots.ProgramFiles ?? string.Empty,
            KnownFolder.ProgramFilesX86 => _roots.ProgramFilesX86 ?? string.Empty,
            KnownFolder.WindowsEtc => _roots.WindowsEtc ?? string.Empty,
            _ => throw new RecipePathException($"unknown known-folder {folder}"),
        };
        if (string.IsNullOrWhiteSpace(raw))
            throw new RecipePathException($"known-folder {folder} resolved to an empty path");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
    }

    /// <summary>
    /// Resolve <paramref name="relative"/> under <paramref name="folder"/> to an absolute path, rejecting any
    /// attempt to escape the chosen profile root. Pure-lexical: it does NOT touch disk (the on-disk reparse/
    /// canonicalize containment is enforced later by <see cref="RecipeResolver"/>).
    /// </summary>
    public string Resolve(KnownFolder folder, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            throw new RecipePathException("recipe path is empty");

        // Reject smuggled environment tokens — F1: recipes never carry %ENV% (only the closed enum).
        if (relative.Contains('%'))
            throw new RecipePathException($"recipe path may not contain an environment token: {relative}");

        // Reject rooted / drive-qualified / UNC / device paths — only profile-relative segments are allowed.
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new RecipePathException($"recipe path must be relative (not rooted): {relative}");
        if (relative.StartsWith(@"\\", StringComparison.Ordinal) || relative.StartsWith("//", StringComparison.Ordinal))
            throw new RecipePathException($"recipe path must not be a UNC/device path: {relative}");

        // Reject any '..' traversal segment outright (lexical defense; resolver re-checks canonically too).
        foreach (string seg in relative.Split('/', '\\'))
            if (seg == "..")
                throw new RecipePathException($"recipe path may not contain a parent traversal: {relative}");

        string root = RootFor(folder);
        string combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

        if (!IsContained(root, combined))
            throw new RecipePathException($"recipe path escapes its profile root: {relative}");

        return combined;
    }

    /// <summary>True when <paramref name="candidate"/> is the root itself or strictly inside it (case-insensitive).</summary>
    internal static bool IsContained(string root, string candidate)
    {
        string r = Path.TrimEndingDirectorySeparator(root);
        string c = Path.TrimEndingDirectorySeparator(candidate);
        return string.Equals(r, c, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

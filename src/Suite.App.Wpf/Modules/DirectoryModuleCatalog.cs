using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace WindowsCareKit.App.Modules;

/// <summary>
/// Discovers modules at runtime from <c>&lt;appdir&gt;\Modules\&lt;id&gt;\Suite.Module.&lt;id&gt;.dll</c>,
/// replacing the compile-time module set behind the same <see cref="IModuleCatalog"/> seam.
/// <para>
/// Ratified M4 trust policy (owner Gate3):
/// </para>
/// <list type="bullet">
///   <item>The production load root is FIXED to <c>Path.Combine(AppContext.BaseDirectory, "Modules")</c>.
///     The directory-override constructor is <c>internal</c> (tests only) — no CLI/env/config/registry
///     path can redirect where modules load from.</item>
///   <item>One level of directories only (no recursion, no <c>..</c> traversal). Every candidate is
///     canonicalized with <see cref="Path.GetFullPath(string)"/> and confirmed contained under the
///     canonical root before it is loaded.</item>
///   <item>Exactly one explicit file is loaded per folder: <c>Suite.Module.&lt;folder&gt;.dll</c>. A
///     module-private dep such as <c>Suite.Module.Migration.Recipes.dll</c> never matches the folder
///     name, so it is never scanned as a module — only pulled in as a dependency by
///     <see cref="ModuleAssemblyResolver"/>.</item>
///   <item>Assemblies load into the DEFAULT <see cref="AssemblyLoadContext"/> so the
///     <c>IWckModule</c>/<c>IServiceCollection</c> type identities are shared with the shell and WPF
///     pack-URI name binding resolves. There is NO network access and NO signature/strong-name check in
///     M4: a module directory shares the exe's ACL container — an attacker who can write <c>Modules\</c>
///     can already replace <c>WindowsCareKit.exe</c>, so this adds no new privilege boundary. This is
///     deliberate install-directory trust, NOT a verification step; signing arrives with M6.</item>
///   <item>Every per-folder load step is exception-contained: a corrupt, garbage, duplicate-id, or
///     non-module DLL is skipped with an internal diagnostic and never crashes startup.</item>
/// </list>
/// <para>
/// The shell-owned <see cref="SettingsModule"/> is always appended. The catalog therefore NEVER returns
/// an empty set — even with a missing or empty <c>Modules\</c> folder, the nav rail always has Settings.
/// </para>
/// </summary>
public sealed class DirectoryModuleCatalog : IModuleCatalog
{
    private const string ModuleAssemblyPrefix = "Suite.Module.";
    private const string ReservedSettingsId = "settings";

    private readonly string _modulesRoot;
    private readonly List<string> _diagnostics = new();

    /// <summary>Production entry point: discovers modules from <c>&lt;appdir&gt;\Modules</c> only.</summary>
    public DirectoryModuleCatalog()
        : this(Path.Combine(AppContext.BaseDirectory, "Modules"))
    {
    }

    /// <summary>
    /// Test-only override of the discovery root. Deliberately <c>internal</c> (surfaced to Suite.Tests via
    /// InternalsVisibleTo) so nothing outside the assembly can point the loader at a different folder.
    /// </summary>
    internal DirectoryModuleCatalog(string modulesRoot)
    {
        _modulesRoot = Path.GetFullPath(modulesRoot);
    }

    /// <summary>
    /// Skip reasons recorded during the last <see cref="LoadModules"/> call. Internal only (no logging
    /// infrastructure exists this early in startup, and load failures must not surface attacker-controlled
    /// strings in the UI); tests assert against this.
    /// </summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    public IReadOnlyList<IWckModule> LoadModules()
    {
        _diagnostics.Clear();

        var discovered = new List<IWckModule>();
        if (Directory.Exists(_modulesRoot))
        {
            try
            {
                foreach (string moduleDir in Directory.EnumerateDirectories(_modulesRoot))
                {
                    IWckModule? module = TryLoadModule(moduleDir);
                    if (module is not null)
                        discovered.Add(module);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A filesystem-level failure enumerating the modules root (I/O error, permission, reparse
                // loop) must degrade to the Settings floor, never crash startup (per the trust policy).
                _diagnostics.Add($"skip enumeration: {ex.GetType().Name}");
            }
        }

        // Deterministic, filesystem-order-independent ordering; duplicate ids are impossible because
        // ids are gated to equal the (unique) folder name.
        var ordered = discovered
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        // Structural floor: Settings is shell-owned and always present (even for a missing/empty Modules\).
        ordered.Add(new SettingsModule());
        return ordered;
    }

    private IWckModule? TryLoadModule(string moduleDir)
    {
        string folderName = Path.GetFileName(moduleDir);
        try
        {
            string candidate = Path.GetFullPath(Path.Combine(moduleDir, ModuleAssemblyPrefix + folderName + ".dll"));

            // Containment: the candidate must resolve strictly under the canonical root. This blocks lexical
            // '..' traversal; reparse-point (junction/symlink) redirection is out of scope per the ratified
            // install-dir trust policy (planting a junction here already requires install-dir write).
            string rootPrefix = _modulesRoot + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _diagnostics.Add($"skip '{folderName}': candidate path resolves outside the modules root.");
                return null;
            }

            // Exactly one explicit file per folder; a non-matching name (e.g. a private dep) is never loaded.
            if (!File.Exists(candidate))
            {
                _diagnostics.Add($"skip '{folderName}': no {ModuleAssemblyPrefix}{folderName}.dll present.");
                return null;
            }

            // Register the folder as a private-dep probe path BEFORE the load, so module-private managed deps
            // (e.g. Suite.Module.Migration.Recipes.dll) can be resolved during the type scan and later use.
            ModuleAssemblyResolver.Register(moduleDir);

            Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);

            Type[] exported;
            try
            {
                exported = assembly.GetExportedTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                exported = ex.Types.Where(t => t is not null).ToArray()!;
            }

            List<Type> moduleTypes = exported
                .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IWckModule).IsAssignableFrom(t))
                .ToList();

            if (moduleTypes.Count != 1)
            {
                _diagnostics.Add($"skip '{folderName}': expected exactly one IWckModule implementation, found {moduleTypes.Count}.");
                return null;
            }

            if (Activator.CreateInstance(moduleTypes[0]) is not IWckModule module)
            {
                _diagnostics.Add($"skip '{folderName}': the module type could not be constructed.");
                return null;
            }

            // Identity gate: the id must equal the folder name (unique folders => no duplicate ids, and a
            // dropped DLL cannot impersonate another nav slot) and must not claim the reserved Settings slot.
            if (!string.Equals(module.Id, folderName, StringComparison.OrdinalIgnoreCase))
            {
                _diagnostics.Add($"skip '{folderName}': module id does not match the folder name.");
                return null;
            }

            if (string.Equals(module.Id, ReservedSettingsId, StringComparison.OrdinalIgnoreCase))
            {
                _diagnostics.Add($"skip '{folderName}': '{ReservedSettingsId}' is reserved for the shell.");
                return null;
            }

            return module;
        }
        catch (Exception ex)
        {
            // A malformed/garbage/incompatible DLL (BadImageFormat, ctor throw, missing dep, ...) is an
            // omission, never a crash. Only the exception TYPE is recorded — never a message that could
            // echo an attacker-planted string.
            _diagnostics.Add($"skip '{folderName}': {ex.GetType().Name}.");
            return null;
        }
    }
}

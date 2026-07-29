using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using WindowsCareKit.App.Deployment;

namespace WindowsCareKit.App.Modules;

/// <summary>
/// Discovers modules at runtime from <c>&lt;appdir&gt;\Modules\&lt;id&gt;\Suite.Module.&lt;id&gt;.dll</c>,
/// replacing the compile-time module set behind the same <see cref="IModuleCatalog"/> seam.
/// <para>
/// Ratified M4 trust policy (owner Gate3):
/// </para>
/// <list type="bullet">
///   <item>The production load root is FIXED to <c>Modules\</c> under <see cref="AppLayout.Root"/> — the
///     single owner of the app's layout, which derives that root from resolved process identity alone
///     (never from a marker file, cwd, env, config or registry, and never from a search). The
///     directory-override constructor is <c>internal</c> (tests only), so nothing outside this assembly
///     can redirect where modules load from.</item>
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
///     non-module DLL is skipped and never crashes startup. Its typed outcome reaches the shell UI; the
///     directory label is sanitized first and exception messages/file content are never surfaced.</item>
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

    /// <summary>The module load root's folder name, owned here — the catalog is what a "Modules" folder means.
    /// Internal so the startup preflight can report on the same folder instead of repeating the literal.</summary>
    internal const string ModulesFolderName = "Modules";

    private readonly string _modulesRoot;
    private readonly Func<string, IEnumerable<string>> _enumerateDirectories;

    /// <summary>Production entry point: discovers modules from <c>&lt;appdir&gt;\Modules</c> only.</summary>
    public DirectoryModuleCatalog()
        : this(AppLayout.Current.Resource(ModulesFolderName), Directory.EnumerateDirectories)
    {
    }

    /// <summary>
    /// Test-only override of the discovery root. Deliberately <c>internal</c> (surfaced to Suite.Tests via
    /// InternalsVisibleTo) so nothing outside the assembly can point the loader at a different folder.
    /// </summary>
    internal DirectoryModuleCatalog(string modulesRoot)
        : this(modulesRoot, Directory.EnumerateDirectories)
    {
    }

    /// <summary>Test-only root-enumeration seam. It supplies candidates only and cannot change what counts
    /// as a usable module.</summary>
    internal DirectoryModuleCatalog(
        string modulesRoot,
        Func<string, IEnumerable<string>> enumerateDirectories)
    {
        _modulesRoot = Path.GetFullPath(modulesRoot);
        _enumerateDirectories = enumerateDirectories ?? throw new ArgumentNullException(nameof(enumerateDirectories));
    }

    public ModuleCatalogResult LoadModules()
    {
        var discovered = new List<IWckModule>();
        var records = new List<ModuleComponentRecord>();

        if (!Directory.Exists(_modulesRoot))
            return BuildResult(discovered, ModuleCatalogHealth.FromComponents(_modulesRoot, records));

        IReadOnlyList<string> moduleDirectories;
        try
        {
            // Materialize the root listing before loading any assembly. If enumeration fails partway through,
            // no partial list is presented as a fact and no candidate from that incomplete listing is executed.
            moduleDirectories = _enumerateDirectories(_modulesRoot).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return BuildResult(
                discovered,
                ModuleCatalogHealth.Unavailable(_modulesRoot, ex.GetType().Name));
        }

        foreach (string moduleDir in moduleDirectories)
        {
            IWckModule? module = TryLoadModule(moduleDir, records);
            if (module is not null)
                discovered.Add(module);
        }

        return BuildResult(discovered, ModuleCatalogHealth.FromComponents(_modulesRoot, records));
    }

    private static ModuleCatalogResult BuildResult(
        List<IWckModule> discovered,
        ModuleCatalogHealth health)
    {
        // Deterministic, filesystem-order-independent ordering; duplicate ids are impossible because
        // ids are gated to equal the (unique) folder name.
        var ordered = discovered
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        // Structural floor: Settings is shell-owned and always present (even for a missing/empty Modules\).
        ordered.Add(new SettingsModule());
        return new ModuleCatalogResult(ordered, health);
    }

    private IWckModule? TryLoadModule(
        string moduleDir,
        List<ModuleComponentRecord> records)
    {
        string folderName = Path.GetFileName(moduleDir);
        string displayName = ModuleDirectoryLabel.ForDisplay(folderName);
        try
        {
            string candidate = Path.GetFullPath(Path.Combine(moduleDir, ModuleAssemblyPrefix + folderName + ".dll"));

            // Containment: the candidate must resolve strictly under the canonical root. This blocks lexical
            // '..' traversal; reparse-point (junction/symlink) redirection is out of scope per the ratified
            // install-dir trust policy (planting a junction here already requires install-dir write).
            string rootPrefix = _modulesRoot + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                records.Add(new(
                    displayName,
                    ModuleComponentStatus.Malformed,
                    ModuleCatalogHealth.CategoryOutsideRoot));
                return null;
            }

            // Exactly one explicit file per folder; a non-matching name (e.g. a private dep) is never loaded.
            // Deliberately NOT File.Exists: it swallows UnauthorizedAccessException and returns false, which
            // would report an unreadable folder as Incomplete -- telling the user to reinstall the component
            // when the real fix is a permission. Enumeration throws instead, so the access failure reaches the
            // Unreadable clause below. That is the malformed-vs-unreadable split this whole change exists for.
            if (!Directory.EnumerateFiles(moduleDir, Path.GetFileName(candidate)).Any())
            {
                records.Add(new(displayName, ModuleComponentStatus.Incomplete, null));
                return null;
            }

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

            if (moduleTypes.Count == 0)
            {
                records.Add(new(
                    displayName,
                    ModuleComponentStatus.Malformed,
                    ModuleCatalogHealth.CategoryNoModuleType));
                return null;
            }

            if (moduleTypes.Count > 1)
            {
                records.Add(new(
                    displayName,
                    ModuleComponentStatus.Malformed,
                    ModuleCatalogHealth.CategoryMultipleModuleTypes));
                return null;
            }

            if (Activator.CreateInstance(moduleTypes[0]) is not IWckModule module)
            {
                records.Add(new(
                    displayName,
                    ModuleComponentStatus.Malformed,
                    ModuleCatalogHealth.CategoryNotConstructible));
                return null;
            }

            // Identity gate: the id must equal the folder name (unique folders => no duplicate ids, and a
            // dropped DLL cannot impersonate another nav slot) and must not claim the reserved Settings slot.
            if (!string.Equals(module.Id, folderName, StringComparison.OrdinalIgnoreCase))
            {
                records.Add(new(
                    displayName,
                    ModuleComponentStatus.Malformed,
                    ModuleCatalogHealth.CategoryIdMismatch));
                return null;
            }

            if (string.Equals(module.Id, ReservedSettingsId, StringComparison.OrdinalIgnoreCase))
            {
                records.Add(new(
                    displayName,
                    ModuleComponentStatus.Malformed,
                    ModuleCatalogHealth.CategoryReservedId));
                return null;
            }

            // Publish this directory into the resolver's private-dependency probe set ONLY now that the module has
            // passed EVERY validation gate (containment, single IWckModule, id==folder, not-reserved). A directory that
            // failed any check above returned already and is therefore NEVER registered — a rejected module can no longer
            // pollute the process-global dependency resolver (S2). Its own private deps were resolved during the type
            // scan above from the default probe path; the recipes dep is loaded lazily on first use, after this point.
            ModuleAssemblyResolver.Register(moduleDir);
            records.Add(new(displayName, ModuleComponentStatus.Loaded, null));
            return module;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                      or System.Security.SecurityException
                                      or (IOException and not FileNotFoundException and not DirectoryNotFoundException))
        {
            // The file is present but could not be read: release a lock or fix permissions.
            records.Add(new(displayName, ModuleComponentStatus.Unreadable, ex.GetType().Name));
            return null;
        }
        catch (Exception ex)
        {
            // The file is present but not usable: reinstall it. Only the exception TYPE is recorded — never
            // a message that could echo an attacker-planted string.
            records.Add(new(displayName, ModuleComponentStatus.Malformed, ex.GetType().Name));
            return null;
        }
    }
}

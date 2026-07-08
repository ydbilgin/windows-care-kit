using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace WindowsCareKit.App.Modules;

/// <summary>
/// Resolves module-PRIVATE managed dependencies that live inside a module's own
/// <c>Modules\&lt;id&gt;\</c> folder and are therefore not on the default probing path (today the only
/// one is <c>Suite.Module.Migration.Recipes.dll</c>, the embedded-recipes dep of Migration).
/// <para>
/// Hooked exactly ONCE onto <see cref="AssemblyLoadContext.Default"/>'s <c>Resolving</c> event and hard-
/// gated per the ratified M4 trust policy:
/// </para>
/// <list type="bullet">
///   <item>Only simple names with the ordinal prefix <c>Suite.Module.</c> are ever handled. Any other
///     name (<c>System.*</c>, third-party, a satellite <c>.resources</c> assembly, ...) returns
///     <c>null</c> so the default binder / WPF handle it — the resolver is never a planting oracle for
///     arbitrary assembly names.</item>
///   <item>It probes ONLY the module folders the catalog registered, and only for
///     <c>&lt;Name&gt;.dll</c>; the resolved path is canonicalized and confirmed contained in a
///     registered folder before the load.</item>
///   <item>It loads into the DEFAULT context via <see cref="AssemblyLoadContext.LoadFromAssemblyPath"/> —
///     no network, no <c>byte[]</c> load, no signature check (install-directory trust; M6 adds signing).
///     The whole body is exception-safe and returns <c>null</c> on any failure, so a malformed or
///     planted file can never crash resolution.</item>
/// </list>
/// The hook stays registered for the app lifetime because WPF BAML / pack-URI binds happen lazily, long
/// after the module was first loaded.
/// </summary>
internal static class ModuleAssemblyResolver
{
    private const string ModuleAssemblyPrefix = "Suite.Module.";

    private static readonly object Gate = new();
    private static readonly HashSet<string> RegisteredDirs = new(StringComparer.OrdinalIgnoreCase);
    private static bool _hooked;

    /// <summary>
    /// Registers <paramref name="moduleDir"/> as a private-dependency probe path and hooks the resolver
    /// onto the default context exactly once. Called by the catalog BEFORE it loads a module so the
    /// module's private deps resolve during type-scan.
    /// </summary>
    internal static void Register(string moduleDir)
    {
        string full = Path.GetFullPath(moduleDir);
        lock (Gate)
        {
            RegisteredDirs.Add(full);
            if (!_hooked)
            {
                AssemblyLoadContext.Default.Resolving += OnResolving;
                _hooked = true;
            }
        }
    }

    private static Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name)
    {
        try
        {
            string? simpleName = name.Name;
            if (string.IsNullOrEmpty(simpleName) ||
                !simpleName.StartsWith(ModuleAssemblyPrefix, StringComparison.Ordinal))
                return null;

            string[] dirs;
            lock (Gate)
                dirs = RegisteredDirs.ToArray();

            foreach (string dir in dirs)
            {
                string candidate = Path.GetFullPath(Path.Combine(dir, simpleName + ".dll"));
                string dirPrefix = dir + Path.DirectorySeparatorChar;
                if (!candidate.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
                    continue; // resolved outside the registered folder — refuse
                if (File.Exists(candidate))
                    return context.LoadFromAssemblyPath(candidate);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}

using System.Reflection;
using WindowsCareKit.App.Deployment;
using WindowsCareKit.App.Modules;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Module.Backup;
using WindowsCareKit.Module.Clean;
using WindowsCareKit.Module.Install;
using WindowsCareKit.Module.Migration;
using WindowsCareKit.Module.Restore;
using WindowsCareKit.Module.Uninstall;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Runtime-reflection architecture test: confirms that Suite.Core has no reference to Suite.Execution.
/// Locks the layering contract that BannedApis enforces at compile time — if a future change accidentally
/// adds a Suite.Execution ProjectReference to Suite.Core, this test fails with a clear message.
/// No NetArchTest package needed: plain reflection over GetReferencedAssemblies().
/// </summary>
public class ArchitectureTests
{
    /// <summary>Anchor types, one per feature module assembly. Compile-time references, so a renamed or
    /// deleted module is a build error here rather than a silently shrinking guard.</summary>
    private static readonly Type[] ModuleAnchors =
    [
        typeof(BackupModule), typeof(CleanModule), typeof(InstallModule),
        typeof(MigrationModule), typeof(RestoreModule), typeof(UninstallModule),
    ];

    /// <summary>Every distinct WCK-owned namespace declared in <paramref name="assembly"/>. The
    /// "WindowsCareKit." prefix filter excludes compiler-emitted types the C# compiler injects into every
    /// assembly (Microsoft.CodeAnalysis.EmbeddedAttribute, System.Runtime.CompilerServices.*) and the
    /// null-namespace &lt;PrivateImplementationDetails&gt; type. Do NOT widen this filter: without it the
    /// guard reds on compiler output and would be "fixed" by weakening the real assertion.</summary>
    private static string[] WckNamespaces(Assembly assembly) =>
        assembly.GetTypes()
            .Select(type => type.Namespace)
            .Where(ns => ns is not null && ns.StartsWith("WindowsCareKit.", StringComparison.Ordinal))
            .Select(ns => ns!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(ns => ns, StringComparer.Ordinal)
            .ToArray();

    /// <summary>NAME-01: physical ownership and namespaces must agree. Every WCK type compiled into a feature
    /// module assembly declares that module's own root — a module may not declare a shell namespace. Asserted
    /// over real assembly metadata (what actually ships), not over source text.
    /// <para>Suite.Module.Migration.Recipes is deliberately absent: it declares the Core-domain namespace
    /// WindowsCareKit.Core.Modules.Migration as a documented exception (see BuiltinRecipeSource.cs:3-6 and
    /// ARCH-FIX-SPEC-NAME01 §2.3). The module list is explicit rather than a wildcard scan precisely so that
    /// exception is visible here instead of silent.</para></summary>
    [Theory]
    [InlineData("Suite.Module.Backup", "WindowsCareKit.Module.Backup")]
    [InlineData("Suite.Module.Clean", "WindowsCareKit.Module.Clean")]
    [InlineData("Suite.Module.Install", "WindowsCareKit.Module.Install")]
    [InlineData("Suite.Module.Migration", "WindowsCareKit.Module.Migration")]
    [InlineData("Suite.Module.Restore", "WindowsCareKit.Module.Restore")]
    [InlineData("Suite.Module.Uninstall", "WindowsCareKit.Module.Uninstall")]
    public void Module_assembly_declares_only_its_own_namespace_root(string assemblyName, string expectedRoot)
    {
        Assembly module = ModuleAnchors
            .Select(anchor => anchor.Assembly)
            .Single(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal));

        string[] foreign = WckNamespaces(module)
            .Where(ns => !string.Equals(ns, expectedRoot, StringComparison.Ordinal)
                      && !ns.StartsWith(expectedRoot + ".", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            foreign.Length == 0,
            $"{assemblyName} must declare only namespaces rooted at '{expectedRoot}' (NAME-01: physical " +
            $"ownership and namespaces must agree). Foreign namespaces found: [{string.Join(", ", foreign)}]");
    }

    /// <summary>The converse fence: the shell and the abstraction assembly must not claim a module namespace.
    /// Without this, "ownership agrees" could be satisfied by moving shell types into a module root.</summary>
    [Theory]
    // Suite.App.Wpf.csproj sets <AssemblyName>WindowsCareKit</AssemblyName>, which is the identity reflection sees.
    [InlineData("WindowsCareKit")]
    [InlineData("Suite.App.Abstractions")]
    public void Shell_assembly_declares_no_module_namespace(string assemblyName)
    {
        Assembly shell = assemblyName == "WindowsCareKit"
            ? typeof(DirectoryModuleCatalog).Assembly
            : typeof(AppLayout).Assembly;

        Assert.Equal(assemblyName, shell.GetName().Name);

        string[] moduleNamespaces = WckNamespaces(shell)
            .Where(ns => ns.StartsWith("WindowsCareKit.Module.", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            moduleNamespaces.Length == 0,
            $"{assemblyName} must not declare a feature-module namespace. Found: [{string.Join(", ", moduleNamespaces)}]");
    }

    [Fact]
    public void Suite_Core_does_not_reference_Suite_Execution()
    {
        // Use a well-known Core type so the assembly is definitely loaded.
        System.Reflection.Assembly coreAssembly = typeof(OperationPlan).Assembly;

        System.Reflection.AssemblyName[] referenced = coreAssembly.GetReferencedAssemblies();

        // xunit 2.9.x: Assert.DoesNotContain(collection, predicate) — no message overload.
        // On failure, print the referenced names manually to make the failure diagnostic.
        string referencedNames = string.Join(", ", referenced.Select(r => r.Name));
        Assert.False(
            referenced.Any(r => string.Equals(r.Name, "Suite.Execution", StringComparison.OrdinalIgnoreCase)),
            $"Suite.Core must not reference Suite.Execution (Core→Execution layering violation). " +
            $"Referenced: [{referencedNames}]");
    }

    [Fact]
    public void Suite_Core_does_not_reference_Suite_App_Abstractions()
    {
        System.Reflection.Assembly coreAssembly = typeof(PayloadRootPolicy).Assembly;
        System.Reflection.AssemblyName[] referenced = coreAssembly.GetReferencedAssemblies();
        string referencedNames = string.Join(", ", referenced.Select(reference => reference.Name));

        Assert.False(
            referenced.Any(reference => string.Equals(
                reference.Name,
                typeof(AppLayout).Assembly.GetName().Name,
                StringComparison.OrdinalIgnoreCase)),
            $"Suite.Core must not reference Suite.App.Abstractions. Referenced: [{referencedNames}]");
    }

    [Fact]
    public void Suite_Core_csproj_does_not_reference_Suite_App_Abstractions()
    {
        string csproj = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Suite.Core", "Suite.Core.csproj"));

        Assert.DoesNotContain("Suite.App.Abstractions", csproj, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every API that hands out the ambient application directory. `AppContext.BaseDirectory` and
    /// `AppDomain.CurrentDomain.BaseDirectory` return the same value, so the fence must name both or the
    /// defect class simply moves to the unnamed sibling.</summary>
    private static readonly string[] AmbientAppDirectoryReads =
        ["AppContext.BaseDirectory", "AppDomain.CurrentDomain.BaseDirectory"];

    [Fact]
    public void No_src_file_outside_AppLayout_reads_the_ambient_app_directory()
    {
        string srcRoot = Path.Combine(FindRepositoryRoot(), "src");
        string[] readers = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(srcRoot, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .Where(path => !path.EndsWith(
                Path.Combine("Suite.App.Abstractions", "Deployment", "AppLayout.cs"),
                StringComparison.OrdinalIgnoreCase))
            // Both APIs return the identical value, so banning only one leaves the headline
            // ("no ambient app-directory reads in src") reinstatable with the build clean.
            .Where(path => AmbientAppDirectoryReads.Any(
                api => File.ReadAllText(path).Contains(api, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(srcRoot, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Empty(readers);
    }

    [Fact]
    public void AppPayloadRoots_reads_the_resolved_root()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Suite.App.Abstractions",
            "Deployment",
            "AppPayloadRoots.cs"));

        Assert.Contains("layout.Root", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BaseDirectory", source, StringComparison.Ordinal);

        // An undetermined layout must propagate its refusal. A `catch` here could swallow it and
        // substitute a fallback root with the whole suite green: the behavioural test exercises
        // For(layout), the composition test runs under a determined host layout, and a fallback
        // need not mention BaseDirectory at all. Source-level is the only place this is pinnable.
        Assert.DoesNotContain("catch", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Suite_Core_source_never_suppresses_RS0030()
    {
        foreach (string path in CoreSourceFiles())
            Assert.DoesNotContain("RS0030", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Suite_Core_source_performs_no_physical_file_mutation()
    {
        string[] forbidden = ["File.Replace(", "File.WriteAllText(", "File.Create("];
        foreach (string path in CoreSourceFiles())
        {
            string source = File.ReadAllText(path);
            foreach (string call in forbidden)
                Assert.DoesNotContain(call, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Suite_Module_Restore_csproj_does_not_reference_Suite_Execution()
    {
        string csproj = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Suite.Module.Restore", "Suite.Module.Restore.csproj"));
        Assert.DoesNotContain("Suite.Execution", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Suite_Module_Uninstall_csproj_does_not_reference_Suite_Execution()
    {
        string csproj = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Suite.Module.Uninstall", "Suite.Module.Uninstall.csproj"));
        Assert.DoesNotContain("Suite.Execution", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Suite_Execution_csproj_does_not_reference_Suite_Win32()
    {
        string csproj = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Suite.Execution", "Suite.Execution.csproj"));
        Assert.DoesNotContain("Suite.Win32", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_composition_constructs_no_shadow_concretes_for_registered_ports()
    {
        string module = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Suite.Module.Migration", "MigrationModule.cs"));
        // DIP-01: the module must resolve IMsiCatalog / IPathCanonicalizer, never `new` a shadow
        // concrete that bypasses its own (Win32MsiCatalog) / the shell's (Win32PathCanonicalizer) registration.
        Assert.DoesNotContain("new Win32PathCanonicalizer(", module, StringComparison.Ordinal);
        Assert.DoesNotContain("new Win32MsiCatalog(", module, StringComparison.Ordinal);
    }

    private static IEnumerable<string> CoreSourceFiles()
        => Directory.EnumerateFiles(
            Path.Combine(FindRepositoryRoot(), "src", "Suite.Core"), "*.cs", SearchOption.AllDirectories);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WindowsCareKit.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("repository root not found");
    }
}

using WindowsCareKit.Core.Planning;
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
}

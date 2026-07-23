namespace WindowsCareKit.Core.Modules.Migration.Conversion;

public abstract record RecipeCapabilityGateResult
{
    private RecipeCapabilityGateResult() { }

    public sealed record Ok : RecipeCapabilityGateResult;

    public sealed record Violation(string Reason) : RecipeCapabilityGateResult;
}

public static class RecipeCapabilityHonestyGate
{
    public static RecipeCapabilityGateResult Evaluate(MigrationRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        // This conversion boundary composes the shared capability predicates in its own required order.
        // BuildPlan independently composes the target facts in execution order; it never trusts this verdict.
        // The gate also sees item kinds, which per-target restore manifests deliberately do not carry.
        // A recipe whose declared restoreTier >= ConfigCopy but which any of these would block is OVER-CLAIMING.
        if (!RestoreCapabilityPolicy.AllowsAutomaticWrite(recipe.RestoreTier))
            return new RecipeCapabilityGateResult.Ok();

        if (!RestoreCapabilityPolicy.AllowsAutomaticWrite(recipe.PortabilityClass))
            return Violate($"recipe '{recipe.Id}' declares {recipe.RestoreTier} but portability is {recipe.PortabilityClass}");

        if (!RestoreCapabilityPolicy.IsProfileRoot(recipe.Detect.KnownFolder))
            return Violate($"recipe '{recipe.Id}' declares {recipe.RestoreTier} for non-profile root {recipe.Detect.KnownFolder}");

        if (recipe.Items.Any(i => RestoreCapabilityPolicy.RequiresInventoryOnlyTier(i.Kind)))
            return Violate($"recipe '{recipe.Id}' declares {recipe.RestoreTier} with a non-profile/manual/export item");

        if (!RestoreCapabilityPolicy.AllowsAutomaticWrite(recipe.Restore.Strategy))
            return Violate($"recipe '{recipe.Id}' declares {recipe.RestoreTier} with unsupported strategy {recipe.Restore.Strategy}");

        return new RecipeCapabilityGateResult.Ok();
    }

    private static RecipeCapabilityGateResult.Violation Violate(string reason)
        => new(reason);
}

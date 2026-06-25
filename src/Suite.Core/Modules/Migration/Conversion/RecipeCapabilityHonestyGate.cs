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

        // Static twin of the per-target blocks in MigrationRestoreRunner.BuildPlan. Each check below mirrors,
        // in declaration order, one runtime block (search those reasons if the runner's line numbers drift):
        //   tier short-circuit  <-> RestoreSkipReason.InventoryOnly (effectiveTier < RestoreTier.ConfigCopy)
        //   portability         <-> RestoreSkipReason.MachineLocked (PortabilityClass != ProfileRelative, the F4 fail-safe)
        //   profile-root        <-> RestoreSkipReason.NonProfileRoot (!IsProfileFolder(KnownFolder))
        //   item-kind/strategy  <-> RestoreSkipReason.UnsupportedStrategy (non-ConfigWrite/MergeAfterInstall)
        // A recipe whose declared restoreTier >= ConfigCopy but which any of these would block is OVER-CLAIMING.
        if (recipe.RestoreTier < RestoreTier.ConfigCopy)
            return new RecipeCapabilityGateResult.Ok();

        if (recipe.PortabilityClass != PortabilityClass.ProfileRelative)
            return Violate($"recipe '{recipe.Id}' declares {recipe.RestoreTier} but portability is {recipe.PortabilityClass}");

        if (!IsProfileFolder(recipe.Detect.KnownFolder))
            return Violate($"recipe '{recipe.Id}' declares {recipe.RestoreTier} for non-profile root {recipe.Detect.KnownFolder}");

        if (recipe.Items.Any(i => i.Kind is RecipeItemKind.MachineRoot or RecipeItemKind.WindowsEtc or RecipeItemKind.ExportCmd or RecipeItemKind.ManualTodo))
            return Violate($"recipe '{recipe.Id}' declares {recipe.RestoreTier} with a non-profile/manual/export item");

        if (recipe.Restore.Strategy is not (RestoreStrategy.ConfigWrite or RestoreStrategy.MergeAfterInstall))
            return Violate($"recipe '{recipe.Id}' declares {recipe.RestoreTier} with unsupported strategy {recipe.Restore.Strategy}");

        return new RecipeCapabilityGateResult.Ok();
    }

    private static RecipeCapabilityGateResult.Violation Violate(string reason)
        => new(reason);

    private static bool IsProfileFolder(KnownFolder folder)
        => folder is KnownFolder.UserProfile or KnownFolder.AppData or KnownFolder.LocalAppData;
}

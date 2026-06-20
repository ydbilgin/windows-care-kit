namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>The reasons a piece of data may be bound to one machine (decision §"sahte-güven önleme").</summary>
[Flags]
public enum MachineLockReason
{
    None = 0,

    /// <summary>Protected with DPAPI (current-user/machine key) — does not decrypt on another machine.</summary>
    Dpapi = 1,

    /// <summary>Contains/keys off a user SID — invalid on a freshly-installed account.</summary>
    Sid = 2,

    /// <summary>Bound to hardware (machine GUID / TPM / volume id).</summary>
    Hardware = 4,

    /// <summary>References an absolute path that will not exist on the new machine.</summary>
    AbsolutePath = 8,
}

/// <summary>The classification result for one recipe/item.</summary>
/// <param name="PortabilityClass">The derived portability class.</param>
/// <param name="Reasons">Why it is machine-locked (empty when profile-relative).</param>
public sealed record MachineContextClassification(PortabilityClass PortabilityClass, MachineLockReason Reasons);

/// <summary>
/// PURE classifier (decision §D, "saf fonksiyon, IO yok"): decides whether a recipe's data is
/// profile-relative or machine-locked, from the recipe's DECLARED portability plus any caller-supplied
/// machine-lock signals (e.g. a known DPAPI path, a recipe marker). Honesty (critic fix F3): there is NO
/// content-based "this blob is secret" magic — DPAPI/SID/hardware are recognized only when the recipe/caller
/// marks them by KNOWN path or flag. When in doubt the result is fail-safe: a machine-lock signal always
/// downgrades the class away from profile-relative, never toward it.
/// </summary>
public static class MachineContextProbe
{
    /// <summary>
    /// Classify from the recipe's declared <see cref="PortabilityClass"/> and any observed lock reasons.
    /// The recipe declaration is the floor; observed reasons can only make it MORE locked, never less.
    /// </summary>
    public static MachineContextClassification Classify(PortabilityClass declared, MachineLockReason observed)
    {
        // Any observed machine-lock signal forces machine-locked regardless of an optimistic declaration.
        if (observed != MachineLockReason.None)
            return new MachineContextClassification(PortabilityClass.MachineLocked, observed);

        // No observed signal → trust the declaration, but never invent confidence beyond it.
        return declared switch
        {
            PortabilityClass.ProfileRelative => new MachineContextClassification(PortabilityClass.ProfileRelative, MachineLockReason.None),
            PortabilityClass.Partial => new MachineContextClassification(PortabilityClass.Partial, MachineLockReason.None),
            PortabilityClass.MachineLocked => new MachineContextClassification(PortabilityClass.MachineLocked, MachineLockReason.None),
            _ => new MachineContextClassification(PortabilityClass.MachineLocked, MachineLockReason.None),
        };
    }

    /// <summary>Classify straight from a recipe (no observed external signals).</summary>
    public static MachineContextClassification Classify(MigrationRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return Classify(recipe.PortabilityClass, MachineLockReason.None);
    }
}

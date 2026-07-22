namespace WindowsCareKit.Core.Modules.Install;

/// <summary>How a checkpoint load resolved. Missing and Loaded may drive planning; Corrupt/Unavailable must not.</summary>
public enum RestoreStateLoadStatus { Missing, Loaded, Corrupt, Unavailable }

/// <summary>The typed result of loading a restore checkpoint (NEW-01).</summary>
public sealed record RestoreStateLoad(RestoreStateLoadStatus Status, RestoreState State)
{
    public static RestoreStateLoad Missing { get; } = new(RestoreStateLoadStatus.Missing, RestoreState.Empty);
    public static RestoreStateLoad Loaded(RestoreState state) => new(RestoreStateLoadStatus.Loaded, state);
    public static RestoreStateLoad Corrupt { get; } = new(RestoreStateLoadStatus.Corrupt, RestoreState.Empty);
    public static RestoreStateLoad Unavailable { get; } = new(RestoreStateLoadStatus.Unavailable, RestoreState.Empty);

    /// <summary>True only when a resume plan may be built from <see cref="State"/> (Missing = fresh, Loaded = resume).</summary>
    public bool CanPlanResume => Status is RestoreStateLoadStatus.Missing or RestoreStateLoadStatus.Loaded;
}

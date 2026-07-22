namespace WindowsCareKit.Core.Modules.Install;

/// <summary>
/// Persists and reloads the <see cref="RestoreState"/> checkpoint (<c>.kurulum_state.json</c>) so a
/// reboot mid-restore can resume. Reading and writing this small JSON file is the only IO.
/// </summary>
public interface IRestoreStateStore
{
    /// <summary>Loads a typed checkpoint outcome so policy callers can distinguish absence from read failure.</summary>
    RestoreStateLoad TryLoad(string stateDirectory);

    /// <summary>
    /// Lenient: collapses Corrupt / Unavailable to <see cref="RestoreState.Empty"/>. Callers that must
    /// distinguish absence from corruption MUST use <see cref="TryLoad"/>.
    /// </summary>
    RestoreState Load(string stateDirectory);

    /// <summary>Writes the checkpoint into <c>&lt;stateDirectory&gt;\.kurulum_state.json</c> (atomically where possible).</summary>
    void Save(string stateDirectory, RestoreState state);

    /// <summary>The full path of the checkpoint file for a directory.</summary>
    string PathFor(string stateDirectory);
}

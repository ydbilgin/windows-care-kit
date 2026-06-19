namespace WindowsCareKit.Core.Execution;

/// <summary>
/// Opens a folder in the file manager (read-only UI affordance — e.g. the Clean module's
/// "open extension folder"). This is NOT a destructive <see cref="Planning.OperationPlan"/> action, so it
/// does not go through the executor / approval flow; it only ever opens an existing directory. The
/// implementation lives in the sanctioned <c>Suite.Execution</c> layer (it launches Explorer); the
/// interface is here so the App can depend on it via DI without referencing the destructive layer.
/// </summary>
public interface IFolderOpener
{
    /// <summary>Opens <paramref name="path"/> in Explorer when it is an existing directory; otherwise no-op.</summary>
    void OpenFolder(string path);
}

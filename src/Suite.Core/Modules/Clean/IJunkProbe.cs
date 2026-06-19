namespace WindowsCareKit.Core.Modules.Clean;

/// <summary>
/// A junk/temporary folder the scanner may propose deleting. Read-only metadata only — the path,
/// an approximate size, and a human note. Discovered by an <see cref="IJunkProbe"/>; never deleted
/// here (deletion is a gated <c>FileDeleteAction</c> built by <see cref="JunkScanner"/>).
/// </summary>
/// <param name="Path">The absolute folder path.</param>
/// <param name="ApproxBytes">Best-effort byte estimate (0 when unknown). Never blocks on a slow walk.</param>
/// <param name="Note">Why this folder is considered junk (e.g. "User temp folder").</param>
public sealed record JunkCandidate(string Path, long ApproxBytes, string Note);

/// <summary>
/// Read-only discovery of junk/temporary folders: the user/Windows temp dirs and browser cache
/// folders. It enumerates and sizes folders; it never deletes (spec §1.2). The destructive step is a
/// gated <c>FileDeleteAction</c> emitted by <see cref="JunkScanner"/>.
/// </summary>
public interface IJunkProbe
{
    /// <summary>Temp, <c>%LocalAppData%\Temp</c>, and browser cache folders (by folder, read-only).</summary>
    IReadOnlyList<JunkCandidate> FindJunk();
}

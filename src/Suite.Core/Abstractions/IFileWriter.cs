namespace WindowsCareKit.Core.Abstractions;

/// <summary>
/// Narrow Core-owned WRITE port for app-owned metadata files. Inner policy declares the intent to persist a
/// small, Suite-owned JSON/markdown file; the physical mutation is performed by the sanctioned outer adapter
/// in Suite.Execution. Core never calls File.WriteAllText / File.Replace / File.Create / Directory.CreateDirectory
/// directly — an API blacklist must not become the de-facto definition of an effect (NEW-02).
/// </summary>
public interface IFileWriter
{
    /// <summary>Create the parent directory if needed and write <paramref name="contents"/> as UTF-8 (no BOM),
    /// overwriting any existing file at <paramref name="path"/>.</summary>
    void WriteAllText(string path, string contents);

    /// <summary>
    /// Atomically replace an APP-OWNED state file: write to an unguessable sibling staging file
    /// (CSPRNG token + <c>.wcktmp</c> suffix, <c>FileMode.CreateNew</c>, reparse-point refusal), then
    /// <c>File.Replace</c> it onto <paramref name="path"/> so a crash cannot lose the existing file.
    /// </summary>
    void AtomicReplace(string path, string contents);
}

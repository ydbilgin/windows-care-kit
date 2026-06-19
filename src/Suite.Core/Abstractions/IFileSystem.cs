namespace WindowsCareKit.Core.Abstractions;

/// <summary>
/// A deliberately NARROW, read-only file-system port (no extra NuGet dependency). It exposes only the
/// existence/enumeration/read primitives the domain needs for planning and verification — never any
/// destructive operation (delete/move/write), which stay behind the sanctioned executor and the SafetyGate.
/// </summary>
public interface IFileSystem
{
    /// <summary>True if a file exists at <paramref name="path"/>.</summary>
    bool FileExists(string path);

    /// <summary>True if a directory exists at <paramref name="path"/>.</summary>
    bool DirectoryExists(string path);

    /// <summary>Open a file for reading. Caller disposes the stream.</summary>
    System.IO.Stream OpenRead(string path);

    /// <summary>
    /// Enumerate file paths under <paramref name="root"/>. When <paramref name="recursive"/> is true,
    /// descends into subdirectories; otherwise lists only the top level.
    /// </summary>
    IEnumerable<string> EnumerateFiles(string root, bool recursive);
}

/// <summary>
/// The production implementation over the real OS file system. Every member maps to a read-only BCL call
/// (<see cref="File.Exists(string)"/>, <see cref="Directory.Exists(string)"/>,
/// <see cref="File.OpenRead(string)"/>, <see cref="Directory.EnumerateFiles(string,string,SearchOption)"/>),
/// none of which is banned.
/// </summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public System.IO.Stream OpenRead(string path) => File.OpenRead(path);

    public IEnumerable<string> EnumerateFiles(string root, bool recursive)
        => Directory.EnumerateFiles(
            root,
            "*",
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
}

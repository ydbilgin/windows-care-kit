using System.Security.Cryptography;
using System.Text;
using WindowsCareKit.Core.Abstractions;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>Sanctioned physical writer for small app-owned metadata files.</summary>
public sealed class SanctionedFileWriter : IFileWriter
{
    /// <inheritdoc />
    public void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <inheritdoc />
    public void AtomicReplace(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Unpredictable, per-write staging name: an attacker cannot pre-plant a reparse point at a name they cannot
        // guess, and CreateNew fails if the exact name already exists (so a pre-planted file/link is DETECTED, not
        // followed). The random token comes from a CSPRNG (S10).
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string staging = $"{path}.{token}.wcktmp";

        // CreateNew: throws if the name already exists (pre-planted file/link). FileShare.None locks it while we write.
        using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            // Verify the just-created object is a REAL file, not a reparse point, before writing sensitive state.
            if (File.GetAttributes(staging).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"Refusing to write restore state through a reparse point: {staging}");

            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(contents);
        }

        if (!File.Exists(path))
            using (File.Create(path)) { }

#pragma warning disable RS0030 // Sanctioned own-file atomic checkpoint seam: replace only the Suite-owned restore state file.
        File.Replace(staging, path, destinationBackupFileName: null);
#pragma warning restore RS0030
    }
}

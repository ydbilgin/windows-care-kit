using System.Security.Cryptography;

namespace WindowsCareKit.Core.Abstractions;

/// <summary>
/// Computes content hashes for backup/verify flows (e.g. proving a copied file matches its source).
/// A port so tests can supply deterministic digests without touching the disk.
/// </summary>
public interface IHasher
{
    /// <summary>
    /// The lowercase-hex SHA-256 of the file's bytes at <paramref name="path"/>.
    /// </summary>
    string ComputeFileSha256(string path);
}

/// <summary>
/// The production hasher: streams the file through SHA-256 and returns lowercase hex.
/// <see cref="File.OpenRead(string)"/> + <see cref="SHA256"/> are read-only and not banned.
/// </summary>
public sealed class Sha256Hasher : IHasher
{
    public string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

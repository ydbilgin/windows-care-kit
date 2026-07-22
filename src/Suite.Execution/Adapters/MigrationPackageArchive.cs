using System.IO.Compression;
using WindowsCareKit.Core.Modules.Migration;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// A sanctioned <c>System.IO.Compression</c> adapter over a migration package. It zips a package folder to a
/// <c>.zip</c> and unzips a <c>.zip</c> back into a package folder while rejecting Zip-Slip entries.
/// </summary>
public static class MigrationPackageArchive
{
    /// <summary>Zip the whole <paramref name="packageDirectory"/> tree into <paramref name="zipPath"/>.</summary>
    public static void Export(string packageDirectory, string zipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        if (!Directory.Exists(packageDirectory))
            throw new DirectoryNotFoundException($"package directory not found: {packageDirectory}");

        string? zipDir = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (!string.IsNullOrEmpty(zipDir))
            Directory.CreateDirectory(zipDir);

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));

        using FileStream fs = File.Create(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            ZipArchiveEntry entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
            using Stream es = entry.Open();
            using FileStream src = File.OpenRead(file);
            src.CopyTo(es);
        }
    }

    /// <summary>
    /// Unzip <paramref name="zipPath"/> into <paramref name="destinationDirectory"/>, rejecting any entry that
    /// would escape the destination root (Zip-Slip defense, fail-closed).
    /// </summary>
    public static void Import(string zipPath, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!File.Exists(zipPath))
            throw new FileNotFoundException($"package zip not found: {zipPath}", zipPath);

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        Directory.CreateDirectory(root);

        using FileStream fs = File.OpenRead(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                continue;

            string targetPath = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!RecipePathResolver.IsContained(root, targetPath))
                throw new InvalidOperationException($"zip entry escapes the extraction root (Zip-Slip): {entry.FullName}");

            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using Stream es = entry.Open();
            using FileStream dst = File.Create(targetPath);
            es.CopyTo(dst);
        }
    }
}

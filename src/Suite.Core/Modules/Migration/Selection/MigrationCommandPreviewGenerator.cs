using System.Text;

namespace WindowsCareKit.Core.Modules.Migration.Selection;

/// <summary>
/// Display-only command strings. This type has no process/executor dependency and accepts no recipe command text.
/// </summary>
public static class MigrationCommandPreviewGenerator
{
    public static string? Generate(MigrationSelectionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.SourceKind == MigrationSourceKind.None)
            return null;

        string source = PowerShellLiteral(RequirePath(candidate.SourcePath, "source"));
        string destination = PowerShellLiteral(RequirePath(candidate.DestinationPath, "destination"));

        return candidate.SourceKind switch
        {
            MigrationSourceKind.Directory
                => $"robocopy {source} {destination} /E /COPY:DAT /R:1 /W:1 /XJ",
            MigrationSourceKind.File
                => $"Copy-Item -LiteralPath {source} -Destination {destination} -Force",
            _ => throw new ArgumentOutOfRangeException(nameof(candidate), "unknown migration source kind"),
        };
    }

    public static IReadOnlyList<string> GenerateSelected(IEnumerable<MigrationSelectionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Where(item => item.IsSelected)
            .Select(item => Generate(item.Candidate))
            .Where(command => command is not null)
            .Cast<string>()
            .ToArray();
    }

    private static string RequirePath(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"{name} path is required");
        foreach (char ch in path)
            if (char.IsControl(ch))
                throw new ArgumentException($"{name} path contains a control character");
        return path;
    }

    /// <summary>PowerShell single-quoted literal; embedded apostrophes are doubled, so data cannot break out.</summary>
    private static string PowerShellLiteral(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('\'');
        foreach (char ch in value)
        {
            if (ch == '\'')
                sb.Append("''");
            else
                sb.Append(ch);
        }
        sb.Append('\'');
        return sb.ToString();
    }
}

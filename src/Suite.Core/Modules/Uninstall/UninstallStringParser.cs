namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>A registry uninstall string split into an executable + structured argument list.</summary>
public sealed record ParsedUninstallCommand(string FileName, IReadOnlyList<string> Arguments, bool IsMsi)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(FileName);
}

/// <summary>
/// Splits a raw <c>UninstallString</c> into a file name and an argument list so the official
/// uninstaller can be launched with <c>ProcessStartInfo.ArgumentList</c> — never as a shell string
/// (spec §1.1, §4). Handles quoted paths, unquoted paths with spaces (via the <c>.exe</c> boundary),
/// MsiExec, and rundll32 forms.
/// </summary>
public static class UninstallStringParser
{
    public static ParsedUninstallCommand? Parse(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString))
            return null;

        string s = uninstallString.Trim();

        string fileName;
        string rest;

        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            if (end < 0)
                return null; // unterminated quote
            fileName = s.Substring(1, end - 1);
            rest = s.Substring(end + 1);
        }
        else
        {
            int exeBoundary = FindExecutableBoundary(s);
            if (exeBoundary > 0)
            {
                fileName = s.Substring(0, exeBoundary);
                rest = s.Substring(exeBoundary);
            }
            else
            {
                int sp = s.IndexOf(' ');
                if (sp < 0)
                {
                    fileName = s;
                    rest = string.Empty;
                }
                else
                {
                    fileName = s.Substring(0, sp);
                    rest = s.Substring(sp);
                }
            }
        }

        fileName = fileName.Trim();
        if (fileName.Length == 0)
            return null;

        var args = Tokenize(rest);
        bool isMsi = string.Equals(Path.GetFileNameWithoutExtension(fileName), "msiexec", StringComparison.OrdinalIgnoreCase);

        return new ParsedUninstallCommand(fileName, args, isMsi);
    }

    /// <summary>Index just past the first <c>.exe</c> token that ends at whitespace or end-of-string.</summary>
    private static int FindExecutableBoundary(string s)
    {
        const string ext = ".exe";
        int from = 0;
        while (true)
        {
            int idx = s.IndexOf(ext, from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return -1;
            int after = idx + ext.Length;
            if (after == s.Length || char.IsWhiteSpace(s[after]))
                return after;
            from = idx + 1;
        }
    }

    /// <summary>Quote-aware split of an argument tail into individual arguments.</summary>
    private static List<string> Tokenize(string rest)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(rest))
            return args;

        int i = 0;
        int n = rest.Length;
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        bool has = false;

        while (i < n)
        {
            char c = rest[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                has = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (has)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    has = false;
                }
            }
            else
            {
                current.Append(c);
                has = true;
            }
            i++;
        }

        if (has)
            args.Add(current.ToString());

        return args;
    }
}

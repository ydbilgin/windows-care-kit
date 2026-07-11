using System.Globalization;

namespace WindowsCareKit.Core.Modules.Migration.Selection;

/// <summary>Culture-aware presentation formatter for Migration byte estimates, isolated from Clean internals.</summary>
public static class MigrationByteSizeFormatter
{
    public static string Format(long bytes, CultureInfo? culture = null)
    {
        if (bytes <= 0)
            return "0 B";

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        CultureInfo formatCulture = culture ?? CultureInfo.CurrentCulture;
        return unit == 0
            ? string.Create(formatCulture, $"{(long)size} {units[unit]}")
            : string.Create(formatCulture, $"{size:0.#} {units[unit]}");
    }
}

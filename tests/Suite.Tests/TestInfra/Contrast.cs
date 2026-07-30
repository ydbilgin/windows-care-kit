using System.Globalization;

namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// WCAG 2.x relative-luminance and contrast-ratio arithmetic, in one place.
/// <para>
/// It lives here rather than privately inside a single test class because two guards now depend on it — the
/// theme palette floors and the legacy chip-ink floor — and two copies of a contrast formula is exactly the
/// kind of duplication that lets one copy be "fixed" until a failing pair passes.
/// </para>
/// </summary>
internal static class Contrast
{
    /// <summary>Contrast ratio between two <c>#RRGGBB</c> or <c>#AARRGGBB</c> colours, 1.0 to 21.0.
    /// Alpha is ignored: these are opaque UI surfaces, and blending a wash would make the assertion depend on
    /// whatever happens to be behind it.</summary>
    public static double Ratio(string foreground, string background)
    {
        double fg = RelativeLuminance(foreground);
        double bg = RelativeLuminance(background);
        double lighter = Math.Max(fg, bg);
        double darker = Math.Min(fg, bg);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static double RelativeLuminance(string hex)
    {
        string rgb = hex.Length == 9 ? hex[3..] : hex[1..];
        double r = Linear(Channel(rgb[0..2]));
        double g = Linear(Channel(rgb[2..4]));
        double b = Linear(Channel(rgb[4..6]));
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    private static double Channel(string pair)
        => byte.Parse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;

    private static double Linear(double channel)
        => channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
}

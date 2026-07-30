namespace WindowsCareKit.App.Controls;

/// <summary>
/// The Segoe Fluent Icons codepoints this design system uses, named once (P22 — no bare escape at a call
/// site). Iconography is restricted to Segoe Fluent Icons / Segoe MDL2 Assets; emoji are not part of the
/// vocabulary (decision §2.4). Written as escapes rather than literal glyphs so the values stay reviewable
/// and survive any encoding round-trip of this file.
/// </summary>
public static class ChipGlyphs
{
    /// <summary>Info — states a fact, claims nothing about safety.</summary>
    public const string Info = "\uE946";

    /// <summary>Undo — the row can be put back.</summary>
    public const string Undo = "\uE7A7";

    /// <summary>Warning — attention, including a partial or degraded read.</summary>
    public const string Warning = "\uE7BA";

    /// <summary>Blocked — irreversible, blocked or failed.</summary>
    public const string Error = "\uEA39";

    /// <summary>Lock — no recovery path exists. Deliberately not the blocked glyph: absence of undo is a
    /// fact about the action, stated calmly, not an alarm (decision §2.1 UndoLine).</summary>
    public const string Locked = "\uE72E";

    /// <summary>View — a preview that has changed nothing.</summary>
    public const string Preview = "\uE890";

    /// <summary>Stopwatch — work in progress.</summary>
    public const string Running = "\uE916";

    /// <summary>Check mark — completed.</summary>
    public const string Done = "\uE73E";
}

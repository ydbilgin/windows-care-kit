namespace WindowsCareKit.App.Controls;

/// <summary>
/// The machine state of a screen, shown beside its H1 (decision §2.1 StatePill). A typed enum rather than a
/// rendered string so a view model can expose the state and a counter can reason about it without parsing
/// display text.
/// </summary>
public enum StatePillState
{
    /// <summary>A plan may exist, but nothing has been changed yet.</summary>
    Preview = 0,

    /// <summary>Work is in progress; Cancel stays enabled throughout.</summary>
    Running = 1,

    /// <summary>The run finished. The pill states the fact; the receipt states the outcome per row —
    /// this is never a success banner over a partial result (honesty invariant §4-8).</summary>
    Done = 2,
}

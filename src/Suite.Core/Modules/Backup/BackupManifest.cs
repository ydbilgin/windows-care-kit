namespace WindowsCareKit.Core.Modules.Backup;

/// <summary>
/// How a manifest entry's <c>source</c> should be treated with respect to secrets (spec §1.3). The
/// Backup planner reads this to decide whether to emit a <see cref="Planning.CopyAction"/> at all.
/// </summary>
public static class SecretHandling
{
    /// <summary>Plain, non-secret data — copy it.</summary>
    public const string Normal = "normal";

    /// <summary>Settings that may reference secrets but the file itself is safe to copy (e.g. config JSON).</summary>
    public const string MetadataOnly = "metadata-only";

    /// <summary>Token/credential store (DPAPI / browser password DB). NEVER copied — re-login is the safe path.</summary>
    public const string NeverRead = "never-read";

    /// <summary>The work is entirely manual (a checklist line); no source path is copied.</summary>
    public const string ManualOnly = "manual-only";

    /// <summary>True when the entry must not be copied because reading it would expose a secret.</summary>
    public static bool ForbidsCopy(string? secretHandling)
        => string.Equals(secretHandling, NeverRead, StringComparison.OrdinalIgnoreCase)
        || string.Equals(secretHandling, ManualOnly, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The transfer method a manifest entry declares (spec §1.3). Only <see cref="Copy"/> becomes a typed
/// <see cref="Planning.CopyAction"/>; everything else is listed for the report (and, for installs, the
/// Kur reinstall list) but is never a Backup action.
/// </summary>
public static class BackupMethod
{
    /// <summary>Copy a file/tree into the payload — the only method that produces a <c>CopyAction</c>.</summary>
    public const string Copy = "copy";

    /// <summary>Run an export tool (e.g. <c>netsh wlan export</c>). Listed only; Backup emits no action for it.</summary>
    public const string ExportCmd = "export-cmd";

    /// <summary>A manual checklist item. Listed in MANUAL_TODO; no action.</summary>
    public const string ManualTodo = "manual-todo";

    /// <summary>True when the method is an installer (<c>install-*</c>) — a reinstall-list item, not a Backup action.</summary>
    public static bool IsInstall(string? method)
        => method is not null && method.StartsWith("install-", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One backup manifest entry — the subset of the JSON schema the Backup planner consumes (spec §1.3).</summary>
/// <param name="Id">Stable manifest id, e.g. <c>chrome-profile</c>.</param>
/// <param name="Enabled">When false the entry is skipped (opt-in items default off).</param>
/// <param name="Method">One of <see cref="BackupMethod"/>; only <c>copy</c> yields a <c>CopyAction</c>.</param>
/// <param name="Category">UI grouping, e.g. <c>browser</c>.</param>
/// <param name="Source">The (env-unexpanded) source path for a copy; empty for export/manual entries.</param>
/// <param name="Target">Destination relative to the payload root, e.g. <c>browser/Chrome/Default</c>.</param>
/// <param name="Exclude">Glob-ish exclude patterns surfaced to the report (the engine guards secret leaves itself).</param>
/// <param name="SecretHandling">One of <see cref="SecretHandling"/>; <c>never-read</c>/<c>manual-only</c> forbid copy.</param>
/// <param name="RestoreOrder">Restore ordering hint (from <c>restore.order</c>).</param>
/// <param name="RestoreMode">Restore mode hint (from <c>restore.mode</c>), e.g. <c>merge-after-install</c>.</param>
/// <param name="Description">Human-readable description shown in the dry-run and the report.</param>
/// <param name="UiWarning">Optional red warning (secret/large-state items); null when none.</param>
public sealed record BackupEntry(
    string Id,
    bool Enabled,
    string Method,
    string Category,
    string Source,
    string Target,
    IReadOnlyList<string> Exclude,
    string SecretHandling,
    int RestoreOrder,
    string RestoreMode,
    string Description,
    string? UiWarning)
{
    /// <summary>Absolute source paths (env-expanded) that must never be copied even within an allowed tree
    /// — the manifest's <c>forbiddenSources</c> list (e.g. a browser profile's <c>Web Data</c>). Defense in
    /// depth on top of <see cref="Exclude"/> and the engine's built-in secret-leaf guard (spec §1.3).</summary>
    public IReadOnlyList<string> ForbiddenSources { get; init; } = Array.Empty<string>();

    /// <summary>The manifest's <c>include</c> allow-list (globs relative to <see cref="Source"/>). When present,
    /// ONLY matching paths are copied — everything else is skipped (spec §1.3).</summary>
    public IReadOnlyList<string> Include { get; init; } = Array.Empty<string>();

    /// <summary>True when this entry should produce a <see cref="Planning.CopyAction"/> (enabled copy, no secret).</summary>
    public bool IsCopyable
        => Enabled
        && string.Equals(Method, BackupMethod.Copy, StringComparison.OrdinalIgnoreCase)
        && !Modules.Backup.SecretHandling.ForbidsCopy(SecretHandling);

    /// <summary>True when this entry must be reported as a manual to-do (never-read secret, or a manual-todo method).</summary>
    public bool IsManualTodo
        => Modules.Backup.SecretHandling.ForbidsCopy(SecretHandling)
        || string.Equals(Method, BackupMethod.ManualTodo, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this entry is an installer item that belongs on the Kur reinstall list, not a backup.</summary>
    public bool IsInstall => BackupMethod.IsInstall(Method);
}

/// <summary>A loaded, merged set of backup manifest entries (spec §1.3 manifest-driven).</summary>
/// <param name="Entries">All entries across the ported manifest files, in load order.</param>
public sealed record BackupManifest(IReadOnlyList<BackupEntry> Entries);

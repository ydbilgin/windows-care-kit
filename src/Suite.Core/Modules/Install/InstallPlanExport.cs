using System.Text.Json.Serialization;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Modules.Install;

/// <summary>
/// How one item of the restore plan is classified in the exported <c>install_plan.json</c> (Step 3 EXPORT
/// vertical slice). Mirrors the planner's three output channels — the executable actions, the reported
/// skips, and the manual checklist — flattened into a single, host-portable list.
/// </summary>
public enum InstallItemClass
{
    /// <summary>A winget/npm install action (re-acquire the app from its package source).</summary>
    Reinstall,

    /// <summary>A config-restore action (copy/merge a configuration file back).</summary>
    Copy,

    /// <summary>A sign-in the user performs by hand after install. ONLY the short auth key + class travel
    /// off-machine — never the probe path, the sign-in command, or any secret (locked decision #3).</summary>
    Login,

    /// <summary>An <c>install-url-manual</c> entry — a download link the user opens by hand.</summary>
    ManualUrl,

    /// <summary>A <c>manual-after</c> entry (login wall, big download, reboot, UAC) run by hand after the auto phase.</summary>
    ManualAfter,

    /// <summary>An entry that did not become an executable action and is not a manual step — excluded with a reason
    /// (already-done, non-Net driver, incomplete, or gate-blocked).</summary>
    Excluded,
}

/// <summary>
/// One classified item in the exported install plan. This is a MINIMAL, WCK-native projection (locked
/// decision #1): it carries only what the export needs and never the winget-import schema (that is Step 4).
/// The redaction discipline (locked decision #3) is enforced at construction in <see cref="InstallPlanExport.Build"/>
/// for EVERY class, not only Login: <see cref="Description"/> is built from a path-free safe label (the entry id,
/// the allow-listed package id, or — for Login — the short auth key). It NEVER carries a file-system path, a config
/// source/destination, a sign-in command, a manual URL, or any secret, and it is NEVER copied from the planner's
/// UI text (which embeds the destination path). The other carried fields are likewise path/secret-free: the winget
/// id and npm package are allow-listed package identifiers, the method is a fixed token, and the skip reason is an
/// enum name.
/// <para>
/// WHITELIST GUARANTEE (systemic — so this is never leak-whack-a-mole again): EVERY serialized field is one of just
/// four categories, or null. No raw manifest string (ConfigSource/Destination, AuthProbe, AuthCommand, ManualUrl,
/// Note, or entry.Description) may enter ANY field:
/// <list type="bullet">
/// <item>(a) an enum — <see cref="Class"/>;</item>
/// <item>(b) a type-safe primitive — <see cref="RequiresAdmin"/> (bool), <see cref="RestoreOrder"/> (int);</item>
/// <item>(c) an allow-list-validated value — <see cref="WingetId"/> / <see cref="NpmPackage"/>;</item>
/// <item>(d) a derived constant — <see cref="Method"/> (only a fixed <see cref="InstallMethod"/> token or ""),
/// <see cref="Description"/> (a path-free safe label, or for Login the short auth key), <see cref="SkipReason"/>
/// (an enum name).</item>
/// </list>
/// The TWO by-contract exceptions travel VERBATIM and are UNTRUSTED (see the invariant below): <see cref="EntryId"/>
/// (the correlation key) and — for the Login class only — the short auth key carried in <see cref="Description"/>
/// (the intended sign-in payload, locked decision #3).
/// </para>
/// <para>
/// UNTRUSTED-PASSTHROUGH INVARIANT (present-tense, not merely a future concern): EntryId, the Login auth key, and the
/// Login <see cref="Description"/> are NOT redacted and NOT shape-checked — they are copied verbatim from the manifest
/// into install_plan.json ON DISK. A hostile or malformed manifest can therefore place a filesystem path or a secret
/// in these fields TODAY, and that value is written to the exported file (which the user may sync, share, or carry in
/// a migration bundle). This is a deliberate contract boundary, NOT a redaction guarantee. Any consumer that reads
/// install_plan.json back — a future Import gate, or anything that treats these as a path, command, lookup key, or
/// credential — MUST shape-validate them first. Regression anchors: InstallPlanExportRedTeamTests pin the
/// path-in-EntryId and secret-in-AuthKey cases.
/// </para>
/// </summary>
/// <param name="EntryId">The manifest entry id this item came from (correlates back to the plan). UNTRUSTED verbatim
/// passthrough — NOT inherently safe: copied as-is into install_plan.json on disk, so a hostile manifest can place a
/// path/secret here. Any importer MUST shape-validate it before use (see the UNTRUSTED-PASSTHROUGH INVARIANT on
/// InstallPlanItem).</param>
/// <param name="Class">How the item is classified for the user.</param>
/// <param name="Method">The original manifest method token (<c>install-winget</c>/<c>install-npm</c>/<c>config-restore</c>/…) or empty.</param>
/// <param name="WingetId">The winget package id, when this is a winget reinstall (allow-listed at plan time); otherwise null.</param>
/// <param name="NpmPackage">The npm package name, when this is an npm reinstall (allow-listed at plan time); otherwise null.</param>
/// <param name="RequiresAdmin">True when the action needs elevation.</param>
/// <param name="RestoreOrder">The deterministic restore-sequence position (spec §1.4), so the export preserves ordering.</param>
/// <param name="SkipReason">For an <see cref="InstallItemClass.Excluded"/> item, why it was skipped; otherwise null.</param>
/// <param name="Description">A short, path-free label built from safe inputs for most classes — EXCEPT (1) for Login
/// it carries the auth key VERBATIM, and (2) when an item's EntryId is itself path/secret-shaped that value flows into
/// this label as-is; both are UNTRUSTED passthrough (see the UNTRUSTED-PASSTHROUGH INVARIANT on InstallPlanItem).</param>
/// <param name="Channel">Forward-looking package channel; NOT populated in this slice (locked decision #2) — left null
/// and omitted from the serialized JSON.</param>
public sealed record InstallPlanItem(
    string EntryId,
    InstallItemClass Class,
    string Method,
    string? WingetId,
    string? NpmPackage,
    bool RequiresAdmin,
    int RestoreOrder,
    string? SkipReason,
    string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Channel = null);

/// <summary>
/// The exported install-plan document — a MINIMAL WCK-native schema (locked decision #1): a schema version,
/// the generation instant, and the flattened, classified items. It deliberately does NOT lean on the
/// winget-import schema (Step 4).
/// </summary>
/// <param name="SchemaVersion">The document schema version (bumped only on a breaking shape change).</param>
/// <param name="GeneratedUtc">When the export was produced (from the injected clock — deterministic in tests).</param>
/// <param name="Items">The classified plan items, in restore order.</param>
public sealed record InstallPlanExportDoc(
    int SchemaVersion,
    DateTime GeneratedUtc,
    IReadOnlyList<InstallPlanItem> Items);

/// <summary>
/// Pure projection of an <see cref="InstallPlanResult"/> into the host-portable <see cref="InstallPlanExportDoc"/>.
/// <see cref="Build"/> reads only the already-built plan (it NEVER re-runs the planner or produces a new gated
/// action — invariant, locked decision #6) and stamps the time from the injected <see cref="IClock"/>, so it is
/// fully unit-testable with zero IO. The three planner channels collapse into one classified list:
/// <list type="bullet">
/// <item>plan actions: <see cref="CommandAction"/> (winget/npm) → <see cref="InstallItemClass.Reinstall"/>,
/// <see cref="RestoreMergeAction"/> (config) → <see cref="InstallItemClass.Copy"/>;</item>
/// <item>skips → <see cref="InstallItemClass.Excluded"/> with the skip reason;</item>
/// <item>manual checklist: an auth-key entry → <see cref="InstallItemClass.Login"/> (key only),
/// a url-manual entry → <see cref="InstallItemClass.ManualUrl"/>, a manual-after entry → <see cref="InstallItemClass.ManualAfter"/>.</item>
/// </list>
/// The skip channel and the manual channel overlap by design (the planner reports a url-manual/manual-after
/// entry in BOTH), so an entry already surfaced from the manual checklist is not duplicated from the skip list.
/// </summary>
public static class InstallPlanExport
{
    /// <summary>The schema version of the exported document. Bump only on a breaking shape change.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Project the built plan result into the export document. Pure: no IO, no new gated action.</summary>
    public static InstallPlanExportDoc Build(InstallPlanResult result, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(clock);

        var items = new List<InstallPlanItem>();

        // Reverse-map each plan action id back to its entry id so the export carries the manifest correlation
        // (the planner already stamped this authoritatively — no positional re-derivation).
        IReadOnlyDictionary<string, string> actionEntry = result.ActionEntryIds;
        // The entry's true deterministic restore position (the action records do not carry it themselves — F3).
        IReadOnlyDictionary<string, int> restoreOrder = result.RestoreOrderByEntryId;

        // 1) The executable actions: winget/npm → Reinstall, config restore → Copy.
        foreach (PlannedAction action in result.Plan.Actions)
        {
            string entryId = actionEntry.TryGetValue(action.Id, out string? e) ? e : action.Id;
            int order = restoreOrder.TryGetValue(entryId, out int o) ? o : 0;
            switch (action)
            {
                case CommandAction cmd:
                    items.Add(FromCommand(entryId, cmd, order));
                    break;
                case RestoreMergeAction:
                    // F1: do NOT carry the planner's UI text ("Restore config to {ConfigDestination}") — it leaks
                    // the destination path. The label is a path-free entry-id + class tag.
                    items.Add(new InstallPlanItem(
                        EntryId: entryId,
                        Class: InstallItemClass.Copy,
                        Method: InstallMethod.ConfigRestore,
                        WingetId: null,
                        NpmPackage: null,
                        RequiresAdmin: false,
                        RestoreOrder: order,
                        SkipReason: null,
                        Description: SafeLabel(InstallItemClass.Copy, entryId)));
                    break;
                // Any other action kind is out of scope for the install export and is intentionally ignored.
            }
        }

        // Track which entry ids are surfaced via the manual checklist so the skip channel does not duplicate
        // a url-manual / manual-after entry (the planner reports them in BOTH lists).
        var manualEntryIds = new HashSet<string>(StringComparer.Ordinal);

        // 2) The manual checklist: classify by what kind of manual step it is.
        foreach (InstallEntry entry in result.ManualChecklist)
        {
            manualEntryIds.Add(entry.Id);
            items.Add(FromManual(entry));
        }

        // 3) The skips → Excluded (+ reason), unless the entry is already a manual item.
        foreach (InstallSkip skip in result.Skipped)
        {
            if (manualEntryIds.Contains(skip.Entry.Id))
                continue;

            // F1: neither the manifest description nor the skip note travels — the note can embed a manual URL and
            // the description is user-authored (may hold a path). The label is a path-free entry-id + class tag; the
            // machine-readable reason is the enum name in SkipReason.
            // F2: a skip's winget id / npm package come STRAIGHT from the manifest (the loader only trims, it never
            // shape-checks), so a hostile manifest can put a path/token in wingetId/npmPackage. Pass both through the
            // SAME allow-list that gates the executable action so a non-package-shaped value is dropped to null and
            // never reaches the JSON.
            // F4: the method is ALSO a raw manifest string (loader only trims) — a manual-after/skip entry can carry a
            // path-shaped method. Run it through AllowedMethod so only a known InstallMethod token survives.
            items.Add(new InstallPlanItem(
                EntryId: skip.Entry.Id,
                Class: InstallItemClass.Excluded,
                Method: AllowedMethod(skip.Entry.Method),
                WingetId: AllowedWingetId(skip.Entry.WingetId),
                NpmPackage: AllowedNpmPackage(skip.Entry.NpmPackage),
                RequiresAdmin: skip.Entry.RequiresAdmin,
                RestoreOrder: skip.Entry.RestoreOrder,
                SkipReason: skip.Reason.ToString(),
                Description: SafeLabel(InstallItemClass.Excluded, skip.Entry.Id)));
        }

        var ordered = items
            .OrderBy(i => i.RestoreOrder)
            .ThenBy(i => i.EntryId, StringComparer.Ordinal)
            .ToArray();

        return new InstallPlanExportDoc(SchemaVersion, clock.UtcNow, ordered);
    }

    private static InstallPlanItem FromCommand(string entryId, CommandAction cmd, int restoreOrder)
    {
        // F1: do NOT carry the planner's UI text — build the label from the allow-listed package identifier (a
        // publisher.product id / npm package name, never a path or secret), falling back to the entry id.
        bool isNpm = cmd.FileName.EndsWith("npm.cmd", StringComparison.OrdinalIgnoreCase);
        // The extracted id already comes from a validated --id / package argument; re-run the SAME allow-list anyway
        // so EVERY winget/npm value the export emits passes one gate (consistency + defense-in-depth — F2).
        string? wingetId = AllowedWingetId(ExtractWingetId(cmd));
        string? npmPackage = AllowedNpmPackage(ExtractNpmPackage(cmd));
        string label = (isNpm ? npmPackage : wingetId) ?? entryId;
        return new InstallPlanItem(
            EntryId: entryId,
            Class: InstallItemClass.Reinstall,
            Method: isNpm ? InstallMethod.Npm : InstallMethod.Winget,
            WingetId: wingetId,
            NpmPackage: npmPackage,
            RequiresAdmin: cmd.RequiresElevation,
            RestoreOrder: restoreOrder,
            SkipReason: null,
            Description: SafeLabel(InstallItemClass.Reinstall, label));
    }

    /// <summary>
    /// Build a short label from a class tag + an identifier. The identifier is either an allow-listed package id (safe)
    /// OR a verbatim entry id (UNTRUSTED passthrough — if the manifest entry id is path/secret-shaped it flows into the
    /// label as-is; see the UNTRUSTED-PASSTHROUGH INVARIANT on InstallPlanItem). The label is NEVER copied from the
    /// planner's UI text (which embeds the destination path), so it never leaks the config source/destination. Login is
    /// the one exception, handled in <see cref="FromManual"/>: its label is the short auth key only (also verbatim/UNTRUSTED).
    /// </summary>
    private static string SafeLabel(InstallItemClass cls, string safeIdentifier)
        => $"{cls}: {safeIdentifier}";

    /// <summary>
    /// Return <paramref name="wingetId"/> only when it is a plain, allow-listed winget package id (the SINGLE
    /// source of truth is <see cref="InstallPlanner.IsValidWingetId"/> — the same regex that gates the executable
    /// <c>--id</c> argument); otherwise null. This is the redaction gate for EVERY winget id the export emits, so a
    /// path/token-shaped value from a hostile manifest is dropped and never written to the JSON.
    /// </summary>
    private static string? AllowedWingetId(string? wingetId)
        => InstallPlanner.IsValidWingetId(wingetId) ? wingetId!.Trim() : null;

    /// <summary>
    /// Return <paramref name="npmPackage"/> only when it is a plain, allow-listed npm registry package name (single
    /// source of truth: <see cref="InstallPlanner.IsValidNpmPackage"/>); otherwise null. Redaction gate for EVERY
    /// npm package the export emits — a URL/git/path-shaped value never reaches the JSON.
    /// </summary>
    private static string? AllowedNpmPackage(string? npmPackage)
        => InstallPlanner.IsValidNpmPackage(npmPackage) ? npmPackage!.Trim() : null;

    /// <summary>The closed set of method tokens the export is allowed to emit (the only safe, fixed values).</summary>
    private static readonly HashSet<string> KnownMethods = new(StringComparer.Ordinal)
    {
        InstallMethod.Winget,
        InstallMethod.Npm,
        InstallMethod.UrlManual,
        InstallMethod.ConfigRestore,
    };

    /// <summary>
    /// Return <paramref name="method"/> only when it is EXACTLY one of the known <see cref="InstallMethod"/> tokens
    /// (winget/npm/url-manual/config-restore); otherwise the empty string. The method is a raw manifest value — the
    /// loader only trims it, it never shape-checks — so a hostile <c>method</c> (e.g. a file-system path on a
    /// manual-after/skip entry) would otherwise be copied verbatim into the exported item and serialized off-machine.
    /// This is the redaction gate for EVERY method token the export emits: a value that is not a fixed token is
    /// dropped to "", never written to the JSON. The docstring's "method is a fixed token" promise is now enforced.
    /// </summary>
    private static string AllowedMethod(string? method)
        => method is not null && KnownMethods.Contains(method.Trim()) ? method.Trim() : string.Empty;

    /// <summary>
    /// Pull the winget package id out of the planner's argument list (<c>install --id &lt;id&gt; …</c>). The id was
    /// already allow-listed at plan time, so this just recovers it for the export; null when this is not a winget command.
    /// </summary>
    private static string? ExtractWingetId(CommandAction cmd)
    {
        if (!cmd.FileName.EndsWith("winget.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        int idx = cmd.Arguments.ToList().IndexOf("--id");
        return idx >= 0 && idx + 1 < cmd.Arguments.Count ? cmd.Arguments[idx + 1] : null;
    }

    /// <summary>
    /// Pull the npm package name out of the planner's argument list (<c>install -g --ignore-scripts &lt;pkg&gt;</c>).
    /// It was allow-listed at plan time; this recovers the last argument as the package. Null when not an npm command.
    /// </summary>
    private static string? ExtractNpmPackage(CommandAction cmd)
    {
        if (!cmd.FileName.EndsWith("npm.cmd", StringComparison.OrdinalIgnoreCase))
            return null;
        return cmd.Arguments.Count > 0 ? cmd.Arguments[^1] : null;
    }

    private static InstallPlanItem FromManual(InstallEntry entry)
    {
        // An auth-key entry is a sign-in step (Login). Locked decision #3: ONLY the short auth key + Class=Login
        // travel off-machine — the probe path, the auth command, and any secret are NEVER read into the export.
        if (!string.IsNullOrWhiteSpace(entry.AuthKey))
        {
            return new InstallPlanItem(
                EntryId: entry.Id,
                Class: InstallItemClass.Login,
                Method: AllowedMethod(entry.Method),
                WingetId: null,
                NpmPackage: null,
                RequiresAdmin: entry.RequiresAdmin,
                RestoreOrder: entry.RestoreOrder,
                SkipReason: null,
                Description: entry.AuthKey!.Trim());
        }

        // A url-manual entry is a download link; everything else on the checklist is a manual-after step.
        // F1: the manual URL itself never travels (it can carry a token), and the user-authored description may hold
        // a path — so the label is a path-free entry-id + class tag, NOT entry.Description and NOT the URL.
        // F2: same as the Excluded branch — the manual entry's winget id / npm package are raw manifest values (only
        // trimmed by the loader). Run both through the SAME allow-list that gates the executable action so a
        // path/token-shaped id is dropped to null and never serialized.
        // F4: the method is a raw manifest string too — a manual-after entry's method can be a path. Run it through
        // AllowedMethod so only a known InstallMethod token reaches the JSON. (Classification still uses entry.Method
        // directly: a non-UrlManual value falls to ManualAfter, which is the safe default.)
        bool isUrlManual = string.Equals(entry.Method, InstallMethod.UrlManual, StringComparison.OrdinalIgnoreCase);
        InstallItemClass cls = isUrlManual ? InstallItemClass.ManualUrl : InstallItemClass.ManualAfter;
        return new InstallPlanItem(
            EntryId: entry.Id,
            Class: cls,
            Method: AllowedMethod(entry.Method),
            WingetId: AllowedWingetId(entry.WingetId),
            NpmPackage: AllowedNpmPackage(entry.NpmPackage),
            RequiresAdmin: entry.RequiresAdmin,
            RestoreOrder: entry.RestoreOrder,
            SkipReason: null,
            Description: SafeLabel(cls, entry.Id));
    }
}

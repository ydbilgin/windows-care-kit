using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>
/// Projects a <see cref="LeftoverScanResult"/> into read-only <see cref="LeftoverCandidate"/>s with a
/// CONSERVATIVE ownership tier (spec §6). The split is a hard invariant:
///
/// <list type="bullet">
/// <item><b>Protected</b> — every action the <c>SafetyGate</c> already refused. These arrive as the scan's
/// <see cref="LeftoverScanResult.Skipped"/> list, so the protected tables are NEVER duplicated here; the
/// gate is the single source of truth for "protected".</item>
/// <item><b>ProgramOwned</b> — ONLY three exact cases, attributed to THIS app alone (see
/// <see cref="ClassifyAllowed"/>): (1) the app's own Uninstall registry entry, (2) the EXACT
/// <c>Software\&lt;Publisher&gt;\&lt;DisplayName&gt;</c> vendor leaf, (3) a service whose ImagePath is under the
/// install directory.</item>
/// <item><b>Shared</b> — the DEFAULT for everything else the gate allowed: vendor parent keys, file
/// associations, <c>Installer\Products</c>, App Paths, Run/RunOnce, and any future widened probe source.
/// Display-only; never deletable.</item>
/// </list>
///
/// HONESTY (spec §6): the gate only re-blocks Protected, NOT Shared. The barriers that keep a Shared
/// (non-protected vendor parent) key out of the deletion plan are (a) the live <see cref="LeftoverScanner"/>
/// emitting <see cref="LeftoverScanResult.Plan"/> = ProgramOwned-only, (b) the <see cref="LeftoverPlanBuilder"/>
/// invariant, and (c) PR-4's disabled checkbox — NOT the gate. No claim here says otherwise.
/// </summary>
public sealed class LeftoverClassifier
{
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// The FULL classified candidate list for <paramref name="scan"/> — ProgramOwned + Shared + Protected.
    /// The <see cref="LeftoverScanner"/> already classified the candidates when it produced the result (it must,
    /// to filter <see cref="LeftoverScanResult.Plan"/> down to ProgramOwned only), so this convenience overload
    /// simply returns <see cref="LeftoverScanResult.Candidates"/>. NOTE: it does NOT read
    /// <see cref="LeftoverScanResult.Plan"/>, which is the ProgramOwned-only DELETABLE plan, not the candidate set.
    /// </summary>
    public IReadOnlyList<LeftoverCandidate> Classify(InstalledApp app, LeftoverScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(scan);
        return scan.Candidates;
    }

    /// <summary>
    /// Classify the gate-allowed actions (<paramref name="allowed"/>) and the gate-skipped actions
    /// (<paramref name="skipped"/>). Order is preserved: ProgramOwned/Shared first, then Protected. This is the
    /// seam the live <see cref="LeftoverScanner"/> uses so the classification runs on the SAME data the scan
    /// produced, with no second probe pass (spec §6 PR-3 A).
    /// </summary>
    public IReadOnlyList<LeftoverCandidate> Classify(
        InstalledApp app,
        IReadOnlyList<PlannedAction> allowed,
        IReadOnlyList<SkippedAction> skipped)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(skipped);

        var candidates = new List<LeftoverCandidate>(allowed.Count + skipped.Count);

        foreach (PlannedAction action in allowed)
        {
            (LeftoverClassification cls, string reason) = ClassifyAllowed(app, action);
            candidates.Add(new LeftoverCandidate
            {
                Action = action,
                Classification = cls,
                Selected = false, // opt-in per spec §6: default unchecked even for ProgramOwned
                GateReason = reason,
            });
        }

        // The gate already refused these. They are Protected by definition; we keep the gate's own reason.
        foreach (SkippedAction s in skipped)
        {
            candidates.Add(new LeftoverCandidate
            {
                Action = s.Action,
                Classification = LeftoverClassification.Protected,
                Selected = false,
                GateReason = s.Reason,
            });
        }

        return candidates;
    }

    /// <summary>
    /// Decide ProgramOwned vs Shared for an action the gate ALLOWED. This is the conservative allowlist:
    /// anything that is not provably owned by THIS app falls through to Shared.
    /// </summary>
    private static (LeftoverClassification, string) ClassifyAllowed(InstalledApp app, PlannedAction action) => action switch
    {
        RegistryDeleteAction reg when IsOwnUninstallEntry(app, reg)
            => (LeftoverClassification.ProgramOwned, "The app's own uninstall registry entry"),
        RegistryDeleteAction reg when IsExactVendorLeaf(app, reg)
            => (LeftoverClassification.ProgramOwned, "Exact Software\\<Publisher>\\<DisplayName> vendor leaf"),
        ServiceDeleteAction svc when IsServiceUnderInstallDir(app, svc)
            => (LeftoverClassification.ProgramOwned, "Service whose image path lives under the install directory"),

        // DEFAULT: not provably owned by this app → Shared (display-only, never deletable).
        _ => (LeftoverClassification.Shared, "Shared/context resource — could affect other software"),
    };

    /// <summary>The app's own Uninstall key: HKLM/HKCU per source, exact <c>Uninstall\&lt;RegistryKeyName&gt;</c> leaf.</summary>
    private static bool IsOwnUninstallEntry(InstalledApp app, RegistryDeleteAction reg)
    {
        if (reg.ValueName is not null)
            return false; // a value delete is never the whole-entry attribution
        if (reg.Hive != ExpectedHive(app))
            return false;
        string expected = UninstallRoot + "\\" + app.RegistryKeyName;
        return PathEquals(reg.SubKeyPath, expected);
    }

    /// <summary>
    /// The EXACT <c>Software\&lt;Publisher&gt;\&lt;DisplayName&gt;</c> leaf — NOT the <c>Software\&lt;Publisher&gt;</c>
    /// parent, which is shared with the publisher's other products and stays Shared. The hive is constrained to
    /// the app's scope (HKCU for per-user, HKLM for machine-wide); a vendor-shaped key in HKCR / HKU / the wrong
    /// hive is NOT this app's leaf and stays Shared (PR-3 C — mirrors <see cref="IsOwnUninstallEntry"/>).
    /// </summary>
    private static bool IsExactVendorLeaf(InstalledApp app, RegistryDeleteAction reg)
    {
        if (reg.ValueName is not null)
            return false;
        if (reg.Hive != ExpectedHive(app))
            return false;
        if (string.IsNullOrWhiteSpace(app.Publisher) || string.IsNullOrWhiteSpace(app.DisplayName))
            return false;
        string expected = $"SOFTWARE\\{app.Publisher}\\{app.DisplayName}";
        return PathEquals(reg.SubKeyPath, expected);
    }

    /// <summary>The hive an app's own keys live in: HKCU for per-user installs, HKLM for machine-wide.</summary>
    private static RegistryHive ExpectedHive(InstalledApp app)
        => app.Source == InstalledAppSource.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    /// <summary>
    /// A service is ProgramOwned ONLY when its resolved <see cref="ServiceDeleteAction.ImagePath"/> sits under
    /// the app's install directory (PR-3 B). A non-empty install location alone is NOT enough — a foreign
    /// service whose executable lives elsewhere (or whose image path could not be resolved) stays Shared.
    /// </summary>
    private static bool IsServiceUnderInstallDir(InstalledApp app, ServiceDeleteAction svc)
    {
        if (string.IsNullOrWhiteSpace(app.InstallLocation))
            return false;
        if (string.IsNullOrWhiteSpace(svc.ImagePath))
            return false;
        return IsPathUnder(svc.ImagePath!, app.InstallLocation!);
    }

    /// <summary>Registry subkey comparison: case-insensitive, ignoring surrounding/trailing backslashes.</summary>
    private static bool PathEquals(string? a, string? b)
        => string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    private static string Trim(string? path)
        => (path ?? string.Empty).Trim().Trim('\\');

    /// <summary>
    /// True when <paramref name="candidate"/> equals or sits under <paramref name="root"/> on a path-SEGMENT
    /// boundary (so <c>...\AB</c> does not match <c>...\ABC</c>). Paths are canonicalized first (collapsing
    /// <c>..</c>/<c>.</c> segments) so <c>C:\App\..\Evil</c> does NOT match <c>C:\App</c>. Pure, no filesystem
    /// access, host-safe. Mirrors the Win32 probe's own <c>IsUnder</c> check.
    /// </summary>
    private static bool IsPathUnder(string candidate, string root)
    {
        string c = NormalizePath(candidate);
        string r = NormalizePath(root);
        if (c.Length == 0 || r.Length == 0)
            return false;
        return c.Equals(r, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Canonicalize for containment: trim quotes/whitespace, collapse <c>..</c>/<c>.</c> via
    /// <see cref="System.IO.Path.GetFullPath(string)"/> when possible (no disk access), strip trailing slashes.
    /// Falls back to the raw trimmed value on malformed input.</summary>
    private static string NormalizePath(string? path)
    {
        string p = (path ?? string.Empty).Trim().Trim('"').Trim();
        if (p.Length == 0)
            return string.Empty;
        try { p = System.IO.Path.GetFullPath(p); }
        catch { /* malformed path (invalid chars / not rooted) — keep the trimmed raw form */ }
        return p.TrimEnd('\\');
    }
}

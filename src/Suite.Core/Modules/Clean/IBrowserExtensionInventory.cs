namespace WindowsCareKit.Core.Modules.Clean;

/// <summary>
/// One installed browser extension, for display only. No action is ever emitted from this — extension
/// removal is out of scope (profile/sync risk, spec §1.2). The user can open the extension's folder.
/// </summary>
/// <param name="Browser">The browser family, e.g. "Chrome", "Edge", "Brave".</param>
/// <param name="Profile">The browser profile name, e.g. "Default".</param>
/// <param name="Id">The extension id (the on-disk folder name under <c>Extensions</c>).</param>
/// <param name="Name">The extension's display name when resolvable from its manifest; otherwise null.</param>
/// <param name="FolderPath">The absolute folder for this extension (used by "Open folder").</param>
public sealed record BrowserExtension(string Browser, string Profile, string Id, string? Name, string FolderPath);

/// <summary>
/// The result of enumerating installed browser extensions: the extensions found (partial data preserved), the
/// overall <see cref="SourceHealth"/> across the scanned browser profiles, and a per-failed-profile fault list
/// with SAFE categories (NEW-07). An absent browser or a profile with no Extensions folder is a legitimate
/// empty, not a fault; a profile whose Extensions folder could not be enumerated IS surfaced as Partial. An
/// individual extension whose manifest name is unresolvable still yields a row with a null <c>Name</c> — that
/// is not a source fault (audit Nuance). Named "Listing" (not "Inventory") to avoid clashing with the port.
/// </summary>
public sealed record BrowserExtensionListing(
    IReadOnlyList<BrowserExtension> Extensions,
    SourceHealth Health,
    IReadOnlyList<InventorySourceFault> Faults);

/// <summary>
/// Inventory-only listing of installed browser extensions across Chromium-family browsers. Read-only;
/// removal is intentionally not offered (spec §1.2). No <c>PlannedAction</c> is ever produced from this.
/// </summary>
public interface IBrowserExtensionInventory
{
    /// <summary>Every discoverable extension across the supported browsers and their profiles, with source health.</summary>
    BrowserExtensionListing ReadAll();
}

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
/// Inventory-only listing of installed browser extensions across Chromium-family browsers. Read-only;
/// removal is intentionally not offered (spec §1.2). No <c>PlannedAction</c> is ever produced from this.
/// </summary>
public interface IBrowserExtensionInventory
{
    /// <summary>Every discoverable extension across the supported browsers and their profiles.</summary>
    IReadOnlyList<BrowserExtension> ReadAll();
}

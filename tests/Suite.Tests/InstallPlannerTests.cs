using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>A configurable <see cref="IDriverGuard"/>: returns true only for identifiers it is told are Net.</summary>
internal sealed class FakeDriverGuard : IDriverGuard
{
    public HashSet<string> NetDrivers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsNetClass(string driverIdentifier) => NetDrivers.Contains(driverIdentifier);
}

public class InstallPlannerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static InstallManifestLoader Loader => new();

    private static InstallPlanner Planner(FakeDriverGuard? guard = null)
        => new(TestData.Gate(), guard ?? new FakeDriverGuard());

    private static string ManifestJson(string entries)
        => $$"""{ "schemaVersion": 1, "entries": [ {{entries}} ] }""";

    [Fact]
    public void Winget_auto_entry_becomes_a_command_action()
    {
        var manifest = Loader.Parse(ManifestJson("""
            { "id": "install-chrome", "category": "tarayici", "method": "install-winget",
              "wingetId": "Google.Chrome", "installTier": "auto", "requiresAdmin": false }
            """));

        var result = Planner().BuildPlan(manifest, RestoreState.Empty, T0);

        var cmd = Assert.IsType<CommandAction>(Assert.Single(result.Plan.Actions));
        Assert.EndsWith("winget.exe", cmd.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Google.Chrome", cmd.Arguments[2]);
        Assert.Contains("--silent", cmd.Arguments);
        Assert.Contains("--accept-package-agreements", cmd.Arguments);
        Assert.False(cmd.RequiresElevation);
    }

    [Fact]
    public void RequiresAdmin_winget_entry_requires_elevation()
    {
        var manifest = Loader.Parse(ManifestJson("""
            { "id": "install-ghub", "category": "arac", "method": "install-winget",
              "wingetId": "Logitech.GHUB", "installTier": "auto", "requiresAdmin": true }
            """));

        var cmd = (CommandAction)Planner().BuildPlan(manifest, RestoreState.Empty, T0).Plan.Actions.Single();
        Assert.True(cmd.RequiresElevation);
    }

    [Fact]
    public void Npm_auto_entry_becomes_npm_install_g()
    {
        var manifest = Loader.Parse(ManifestJson("""
            { "id": "install-claude", "category": "ai-cli", "method": "install-npm",
              "npmPackage": "@anthropic-ai/claude-code", "installTier": "auto", "requiresNode": true }
            """));

        var cmd = (CommandAction)Planner().BuildPlan(manifest, RestoreState.Empty, T0).Plan.Actions.Single();
        Assert.EndsWith("npm.cmd", cmd.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "install", "-g", "--ignore-scripts", "@anthropic-ai/claude-code" }, cmd.Arguments);
    }

    [Fact]
    public void Manual_after_entry_is_listed_not_planned()
    {
        var manifest = Loader.Parse(ManifestJson("""
            { "id": "install-steam", "category": "oyun-launcher", "method": "install-winget",
              "wingetId": "Valve.Steam", "installTier": "manual-after", "requiresAdmin": false }
            """));

        var result = Planner().BuildPlan(manifest, RestoreState.Empty, T0);

        Assert.Empty(result.Plan.Actions);
        Assert.Single(result.ManualChecklist);
        Assert.Contains(result.Skipped, s => s.Reason == InstallSkipReason.ManualAfter);
    }

    [Fact]
    public void Url_manual_entry_goes_to_the_manual_checklist()
    {
        var manifest = Loader.Parse(ManifestJson("""
            { "id": "install-nvidia", "category": "arac", "method": "install-url-manual",
              "manualUrl": "https://nvidia.com/app", "installTier": "manual-after", "requiresAdmin": true }
            """));

        var result = Planner().BuildPlan(manifest, RestoreState.Empty, T0);

        Assert.Empty(result.Plan.Actions);
        Assert.Single(result.ManualChecklist);
        Assert.Contains(result.Skipped, s => s.Reason == InstallSkipReason.ManualUrl);
    }

    [Fact]
    public void Driver_entry_is_skipped_unless_class_is_net()
    {
        var manifest = Loader.Parse(ManifestJson("""
            { "id": "driver-realtek", "category": "ag-surucusu", "method": "install-winget",
              "wingetId": "Realtek.NetDriver", "installTier": "auto", "requiresAdmin": true }
            """));

        var blocked = Planner().BuildPlan(manifest, RestoreState.Empty, T0);
        Assert.Empty(blocked.Plan.Actions);
        Assert.Contains(blocked.Skipped, s => s.Reason == InstallSkipReason.DriverNotNet);

        var guard = new FakeDriverGuard();
        guard.NetDrivers.Add("Realtek.NetDriver");
        var allowed = Planner(guard).BuildPlan(manifest, RestoreState.Empty, T0);
        Assert.Single(allowed.Plan.Actions);
    }

    [Fact]
    public void Already_done_entry_is_skipped_on_resume()
    {
        var manifest = Loader.Parse(ManifestJson("""
            { "id": "install-git", "category": "gelistirici", "method": "install-winget",
              "wingetId": "Git.Git", "installTier": "auto" },
            { "id": "install-vscode", "category": "gelistirici", "method": "install-winget",
              "wingetId": "Microsoft.VisualStudioCode", "installTier": "auto" }
            """));

        var state = RestoreState.Empty.With("install-git", RestoreEntryStatus.Done, T0);
        var result = Planner().BuildPlan(manifest, state, T0);

        var cmd = (CommandAction)Assert.Single(result.Plan.Actions);
        Assert.Equal("Microsoft.VisualStudioCode", cmd.Arguments[2]);
        Assert.Contains(result.Skipped, s => s.Reason == InstallSkipReason.AlreadyDone);
    }

    [Fact]
    public void Node_is_ordered_before_ai_cli()
    {
        // ai-cli appears first in the file, but Node (gelistirici) must come first in the restore order.
        var manifest = Loader.Parse(ManifestJson("""
            { "id": "install-claude", "category": "ai-cli", "method": "install-npm",
              "npmPackage": "@anthropic-ai/claude-code", "installTier": "auto", "requiresNode": true },
            { "id": "install-node", "category": "gelistirici", "method": "install-winget",
              "wingetId": "OpenJS.NodeJS.LTS", "installTier": "auto" }
            """));

        var actions = Planner().BuildPlan(manifest, RestoreState.Empty, T0).Plan.Actions;

        var first = (CommandAction)actions[0];
        var second = (CommandAction)actions[1];
        // FileName is now resolved to an absolute path (PATH-hijack defense).
        Assert.EndsWith("winget.exe", first.FileName, StringComparison.OrdinalIgnoreCase);  // Node via winget
        Assert.EndsWith("npm.cmd", second.FileName, StringComparison.OrdinalIgnoreCase);    // AI CLI via npm, after Node
        Assert.True(Path.IsPathFullyQualified(first.FileName));
        Assert.True(Path.IsPathFullyQualified(second.FileName));
    }

    [Fact]
    public void Plan_is_gate_clean_and_uses_the_install_module_name()
    {
        var manifest = Loader.Parse(ManifestJson("""
            { "id": "install-firefox", "category": "tarayici", "method": "install-winget",
              "wingetId": "Mozilla.Firefox", "installTier": "auto" }
            """));

        var plan = Planner().BuildPlan(manifest, RestoreState.Empty, T0).Plan;

        Assert.Equal("install", plan.ModuleName);
        Assert.True(TestData.Gate().Validate(plan).AllAllowed);
    }

    [Fact]
    public void Config_restore_entry_becomes_a_restore_merge_with_bak()
    {
        var manifest = Loader.Parse(ManifestJson("""
            { "id": "restore-gitconfig", "category": "config", "method": "config-restore",
              "installTier": "auto", "source": "C:\\payload\\.gitconfig", "target": "C:\\Users\\alice\\.gitconfig" }
            """));

        var merge = Assert.IsType<RestoreMergeAction>(Planner().BuildPlan(manifest, RestoreState.Empty, T0).Plan.Actions.Single());
        Assert.True(merge.CreateBak);
        Assert.Equal(UndoCapability.Partial, merge.Undo);
        Assert.Equal(@"C:\Users\alice\.gitconfig", merge.Destination);
    }

    [Fact]
    public void ActionEntryIds_stamps_each_action_with_its_originating_entry()
    {
        // Two automatable entries (+ one skipped manual-after) — the correlation must be by id, not position.
        var manifest = Loader.Parse(ManifestJson("""
            { "id": "install-claude", "category": "ai-cli", "method": "install-npm",
              "npmPackage": "@anthropic-ai/claude-code", "installTier": "auto", "requiresNode": true },
            { "id": "install-steam", "category": "oyun-launcher", "method": "install-winget",
              "wingetId": "Valve.Steam", "installTier": "manual-after" },
            { "id": "install-node", "category": "gelistirici", "method": "install-winget",
              "wingetId": "OpenJS.NodeJS.LTS", "installTier": "auto" }
            """));

        var result = Planner().BuildPlan(manifest, RestoreState.Empty, T0);

        // Node (winget) is ordered first, Claude (npm) second; the manual-after Steam is not an action.
        Assert.Equal(2, result.Plan.Actions.Count);
        Assert.Equal(2, result.ActionEntryIds.Count);
        Assert.Equal("install-node", result.ActionEntryIds[result.Plan.Actions[0].Id]);
        Assert.Equal("install-claude", result.ActionEntryIds[result.Plan.Actions[1].Id]);
        // The skipped manual entry must never appear as an action-entry correlation.
        Assert.DoesNotContain("install-steam", result.ActionEntryIds.Values);
    }

    [Fact]
    public void Incomplete_winget_entry_without_id_is_reported_not_planned()
    {
        var manifest = Loader.Parse(ManifestJson("""
            { "id": "install-broken", "category": "arac", "method": "install-winget", "installTier": "auto" }
            """));

        var result = Planner().BuildPlan(manifest, RestoreState.Empty, T0);
        Assert.Empty(result.Plan.Actions);
        Assert.Contains(result.Skipped, s => s.Reason == InstallSkipReason.Incomplete);
    }

    // F3: a crafted wingetId that starts with '-' (could smuggle an extra flag into the --id position) or
    // contains whitespace/slash/other shell-ish characters is rejected exactly like an invalid npm package —
    // it never becomes an action and is reported as Incomplete (same handling as a missing id).
    [Theory]
    [InlineData("--source")]            // leading dash → would read as a winget flag
    [InlineData("-e")]                  // leading dash
    [InlineData("Bad Id With Spaces")]  // whitespace
    [InlineData("evil/../../id")]       // slash / path traversal characters
    [InlineData("a;calc.exe")]          // shell-ish characters
    public void Winget_entry_with_an_invalid_id_is_rejected_not_planned(string wingetId)
    {
        var manifest = Loader.Parse(ManifestJson($$"""
            { "id": "install-crafted", "category": "arac", "method": "install-winget",
              "wingetId": "{{wingetId}}", "installTier": "auto" }
            """));

        var result = Planner().BuildPlan(manifest, RestoreState.Empty, T0);

        Assert.Empty(result.Plan.Actions);
        Assert.Contains(result.Skipped, s => s.Reason == InstallSkipReason.Incomplete);
    }

    [Fact]
    public void Winget_entry_with_a_valid_id_is_accepted()
    {
        var manifest = Loader.Parse(ManifestJson("""
            { "id": "install-powertoys", "category": "arac", "method": "install-winget",
              "wingetId": "Microsoft.PowerToys", "installTier": "auto" }
            """));

        var result = Planner().BuildPlan(manifest, RestoreState.Empty, T0);

        var cmd = Assert.IsType<CommandAction>(Assert.Single(result.Plan.Actions));
        Assert.Equal("Microsoft.PowerToys", cmd.Arguments[2]);
    }
}

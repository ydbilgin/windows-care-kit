using System.IO;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

public class BackupPlannerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static BackupPlanner Planner() => new(TestData.Gate(), new FakeEnvironmentExpander());

    // Under the test policy's current profile (alice) so the hardened write-target gate allows it
    // deterministically; BackupPlanner does no IO on the payload path, so it need not exist.
    private static string OutsidePayloadRoot()
        => Path.Combine(@"C:\Users\alice\wck-backup-tests", Guid.NewGuid().ToString("N"));

    private static BackupEntry CopyEntry(string id, string source, string target,
        bool enabled = true, string secret = SecretHandling.Normal)
        => new(id, enabled, BackupMethod.Copy, "cat", source, target,
            Array.Empty<string>(), secret, 50, "merge-after-install", $"desc {id}", null);

    [Fact]
    public void Copyable_entry_becomes_a_gate_approved_copy_action()
    {
        var manifest = new BackupManifest(new[]
        {
            CopyEntry("docs", @"C:\Users\alice\AppData\Roaming\App", "cat/App"),
        });
        string payload = OutsidePayloadRoot();

        BackupPlanResult result = Planner().BuildPlan(manifest, payload, T0);

        CopyAction copy = Assert.IsType<CopyAction>(Assert.Single(result.Plan.Actions));
        Assert.Equal(@"C:\Users\alice\AppData\Roaming\App", copy.Source);
        Assert.Equal(Path.GetFullPath(Path.Combine(payload, "cat", "App")), copy.Destination);
        Assert.Equal(RiskLevel.Low, copy.Risk);
        Assert.Equal(UndoCapability.None, copy.Undo);
        Assert.True(TestData.Gate().Evaluate(copy).Allowed);
        Assert.Equal("backup", result.Plan.ModuleName);
    }

    [Fact]
    public void Never_read_entries_go_to_manual_not_the_plan()
    {
        var manifest = new BackupManifest(new[]
        {
            CopyEntry("token", @"C:\Users\alice\.codex\auth.json", "ai/auth.json", secret: SecretHandling.NeverRead),
        });

        BackupPlanResult result = Planner().BuildPlan(manifest, OutsidePayloadRoot(), T0);

        Assert.True(result.Plan.IsEmpty);
        BackupEntry manual = Assert.Single(result.ManualTodos);
        Assert.Equal("token", manual.Id);
        Assert.Empty(result.Plan.Actions);
    }

    [Fact]
    public void Never_read_copy_enabled_entry_emits_no_copy_action_even_when_method_is_copy()
    {
        var manifest = new BackupManifest(new[]
        {
            CopyEntry("token", @"C:\Users\alice\.claude.json", "ai/.claude.json", secret: SecretHandling.NeverRead),
        });

        BackupPlanResult result = Planner().BuildPlan(manifest, OutsidePayloadRoot(), T0);

        Assert.Empty(result.Plan.Actions.OfType<CopyAction>());
        Assert.Equal("token", Assert.Single(result.ManualTodos).Id);
    }

    [Fact]
    public void Disabled_entries_are_skipped_with_reason()
    {
        var manifest = new BackupManifest(new[]
        {
            CopyEntry("optin", @"C:\Users\alice\big", "cat/big", enabled: false),
        });

        BackupPlanResult result = Planner().BuildPlan(manifest, OutsidePayloadRoot(), T0);

        Assert.True(result.Plan.IsEmpty);
        BackupSkip skip = Assert.Single(result.Skipped);
        Assert.Equal("optin", skip.Entry.Id);
        Assert.Contains("disabled", skip.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_entries_go_to_the_reinstall_list_never_copied()
    {
        var manifest = new BackupManifest(new[]
        {
            new BackupEntry("vscode", true, "install-winget", "dev", "", "",
                Array.Empty<string>(), SecretHandling.Normal, 10, "command", "VS Code", null),
        });

        BackupPlanResult result = Planner().BuildPlan(manifest, OutsidePayloadRoot(), T0);

        Assert.True(result.Plan.IsEmpty);
        Assert.Equal("vscode", Assert.Single(result.ReinstallList).Id);
    }

    [Fact]
    public void Export_cmd_entries_are_listed_not_actioned()
    {
        var manifest = new BackupManifest(new[]
        {
            new BackupEntry("wifi", true, "export-cmd", "sistem", "", "sistem/wifi",
                Array.Empty<string>(), SecretHandling.MetadataOnly, 5, "command", "WiFi export", null),
        });

        BackupPlanResult result = Planner().BuildPlan(manifest, OutsidePayloadRoot(), T0);

        Assert.True(result.Plan.IsEmpty);
        Assert.Contains(result.Skipped, s => s.Entry.Id == "wifi");
    }

    [Fact]
    public void Payload_inside_app_folder_is_refused_for_every_copy()
    {
        var manifest = new BackupManifest(new[]
        {
            CopyEntry("docs", @"C:\Users\alice\App", "cat/App"),
        });
        // The app folder itself — must be refused (spec §1.3 payload outside the app).
        string insideApp = Path.Combine(AppContext.BaseDirectory, "payload");

        BackupPlanResult result = Planner().BuildPlan(manifest, insideApp, T0);

        Assert.True(result.Plan.IsEmpty);
        Assert.Contains(result.Skipped, s => s.Reason.Contains("outside the app", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Empty_payload_path_is_refused()
    {
        var manifest = new BackupManifest(new[] { CopyEntry("docs", @"C:\Users\alice\App", "cat/App") });

        BackupPlanResult result = Planner().BuildPlan(manifest, "", T0);

        Assert.True(result.Plan.IsEmpty);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void Plan_hash_is_stable_for_the_same_manifest_and_payload()
    {
        var manifest = new BackupManifest(new[] { CopyEntry("docs", @"C:\Users\alice\App", "cat/App") });
        string payload = OutsidePayloadRoot();

        string h1 = Planner().BuildPlan(manifest, payload, T0).Plan.ComputeHash();
        string h2 = Planner().BuildPlan(manifest, payload, T0.AddHours(5)).Plan.ComputeHash();

        Assert.Equal(h1, h2); // hash excludes timestamp/ids
    }
}

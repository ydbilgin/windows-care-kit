using System.IO;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

public class BackupReportWriterTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc);

    // A no-op redactor (no username/profile configured) so the existing assertions that expect raw paths
    // (e.g. "C:\src\a") still pass; the new redaction behavior is covered by the dedicated test below.
    private static BackupReportWriter Writer() => new(new LogRedactor(null, null));

    private static BackupEntry Manual(string id, string desc, string warning, string secret)
        => new(id, true, BackupMethod.Copy, "cat", @"C:\x", "t",
            Array.Empty<string>(), secret, 50, "manual", desc, warning);

    private static BackupEntry Install(string id, string desc)
        => new(id, true, "install-winget", "dev", "", "",
            Array.Empty<string>(), SecretHandling.Normal, 10, "command", desc, null);

    private static BackupPlanResult PlanResult(
        IReadOnlyList<BackupEntry>? manual = null,
        IReadOnlyList<BackupSkip>? skipped = null,
        IReadOnlyList<BackupEntry>? reinstall = null)
        => new(
            new OperationPlan("Back up", "backup", Array.Empty<PlannedAction>(), T0),
            manual ?? Array.Empty<BackupEntry>(),
            skipped ?? Array.Empty<BackupSkip>(),
            reinstall ?? Array.Empty<BackupEntry>());

    [Fact]
    public void Report_lists_copied_skipped_and_reinstall()
    {
        var copyReport = new CopySkipReport(new[]
        {
            new CopyFileOutcome("a", @"C:\src\a", @"D:\pay\a", true, null, "ok"),
            new CopyFileOutcome("b", @"C:\src\b", @"D:\pay\b", false, CopySkipReason.Locked, "IOException: in use"),
        });
        var plan = PlanResult(reinstall: new[] { Install("vscode", "VS Code") });

        string md = Writer().BuildReport(plan, copyReport, T0);

        Assert.Contains("# RAPOR", md);
        Assert.Contains("Copied: 1", md);
        Assert.Contains("Skipped: 1", md);
        Assert.Contains(@"C:\src\a", md);
        Assert.Contains("Locked", md);
        Assert.Contains("vscode", md);          // reinstall list
        Assert.Contains("2026-01-01 12:30:00", md);
    }

    [Fact]
    public void ManualTodo_includes_relogin_guidance_for_secrets()
    {
        var plan = PlanResult(manual: new[]
        {
            Manual("codex-auth", "Codex auth token", "TOKEN — keep off", SecretHandling.NeverRead),
        });

        string md = Writer().BuildManualTodo(plan, T0);

        Assert.Contains("# MANUAL_TODO", md);
        Assert.Contains("codex-auth", md);
        Assert.Contains("Codex auth token", md);
        Assert.Contains("TOKEN — keep off", md);
        Assert.Contains("sign in again", md, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManualTodo_with_nothing_says_so()
    {
        string md = Writer().BuildManualTodo(PlanResult(), T0);
        Assert.Contains("Nothing to do manually", md);
    }

    // L9: WriteReports re-gates the destination. %TEMP% is under the current user's profile → allowed.
    private static ISafetyGate RealGate()
        => new SafetyGate(ProtectedResources.ForCurrentSystem(), new FakeCanonicalizer());

    [Fact]
    public void WriteReports_writes_both_files_into_the_payload_dir()
    {
        string payload = Path.Combine(Path.GetTempPath(), "wck-report-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var plan = PlanResult(manual: new[] { Manual("x", "x desc", "warn", SecretHandling.NeverRead) });
            var copyReport = new CopySkipReport(new[]
            {
                new CopyFileOutcome("a", @"C:\src\a", Path.Combine(payload, "a"), true, null, "ok"),
            });

            (string reportPath, string manualPath) =
                Writer().WriteReports(plan, copyReport, payload, T0, RealGate());

            Assert.True(File.Exists(reportPath));
            Assert.True(File.Exists(manualPath));
            Assert.EndsWith("RAPOR.md", reportPath);
            Assert.EndsWith("MANUAL_TODO.md", manualPath);
            Assert.Contains("Copied: 1", File.ReadAllText(reportPath));
            Assert.Contains("x desc", File.ReadAllText(manualPath));
        }
        finally
        {
            if (Directory.Exists(payload))
                Directory.Delete(payload, recursive: true);
        }
    }

    [Fact]
    public void WriteReports_refuses_a_gate_blocked_destination()
    {
        // A protected system location (Windows directory) is blocked by the gate → nothing is written.
        var plan = PlanResult();
        var copyReport = new CopySkipReport(Array.Empty<CopyFileOutcome>());

        Assert.Throws<UnauthorizedAccessException>(() =>
            Writer().WriteReports(plan, copyReport, @"C:\Windows\wck-evil", T0, RealGate()));
    }

    // F2: the report must not leak the real username/profile path onto external media. A ForCurrentUser-style
    // redactor masks the profile path and user name in every emitted path field (Source/Destination/Detail in
    // RAPOR.md, and the path-bearing id in MANUAL_TODO.md). Authored prose (Description/UiWarning) is emitted
    // as-is, so only the runtime paths are scrubbed.
    [Fact]
    public void Report_redacts_username_and_profile_path_in_path_fields()
    {
        const string user = "alice";
        const string profile = @"C:\Users\alice";
        var redactingWriter = new BackupReportWriter(new LogRedactor(user, profile));

        var copyReport = new CopySkipReport(new[]
        {
            new CopyFileOutcome("doc", @"C:\Users\alice\Documents\a.txt", @"D:\pay\a.txt", true, null, "ok"),
            new CopyFileOutcome("lock", @"C:\Users\alice\.ssh\id_rsa", @"D:\pay\id_rsa", false,
                CopySkipReason.Locked, @"IOException: C:\Users\alice\.ssh\id_rsa in use"),
        });
        // A custom manual-todo whose ID is an absolute profile path (per the backup module's "manuel yol ekleme"
        // custom-entry design — the id field can carry a real path).
        var plan = PlanResult(manual: new[]
        {
            Manual(@"C:\Users\alice\.config\custom", "Sign in again", "warn", SecretHandling.NeverRead),
        });

        string report = redactingWriter.BuildReport(plan, copyReport, T0);
        string manual = redactingWriter.BuildManualTodo(plan, T0);

        // The real profile path and the bare username must not survive into the emitted path fields.
        Assert.DoesNotContain(@"C:\Users\alice", report);
        Assert.DoesNotContain(@"C:\Users\alice", manual);
        Assert.DoesNotContain("alice", report);
        Assert.DoesNotContain("alice", manual);
        // The redaction placeholder appears instead.
        Assert.Contains("%USERPROFILE%", report);
        Assert.Contains("%USERPROFILE%", manual);
        // Sanity: the non-sensitive destination drive still appears (only the profile/user was masked).
        Assert.Contains(@"D:\pay\a.txt", report);
    }
}

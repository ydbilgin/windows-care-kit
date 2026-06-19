using System.Text.Json;
using WindowsCareKit.Core.Logging;
using Xunit;

namespace WindowsCareKit.Tests;

public class LoggingTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Redactor_masks_profile_path_username_and_wifi_key()
    {
        var r = new LogRedactor("alice", @"C:\Users\alice");

        Assert.Equal(@"%USERPROFILE%\AppData", r.Redact(@"C:\Users\alice\AppData"));
        Assert.Equal("hello %USER% bye", r.Redact("hello alice bye"));
        Assert.Equal("key=[REDACTED]", r.Redact("key=Hunter2Secret"));

        // netsh wlan export prints the PSK as "Key Content : <psk>" (and "Anahtar İçeriği" on Turkish
        // Windows) — the value must be masked, even with spaces, to end of line.
        AssertMasked(r, "Key Content : My Wifi Pass Phrase", "My Wifi Pass Phrase");
        AssertMasked(r, "Anahtar İçeriği : GizliParola123", "GizliParola123");

        // Command-argument secrets must not reach the log either (M8).
        AssertMasked(r, "--token=eyJhbGciOiJ.secret.value", "eyJhbGciOiJ.secret.value");
        AssertMasked(r, "Authorization: Bearer abc.def.ghi", "abc.def.ghi");
        AssertMasked(r, "password=correct horse battery", "correct horse battery");
        AssertMasked(r, "apikey=sk-deadbeef", "sk-deadbeef");
    }

    private static void AssertMasked(LogRedactor r, string input, string secret)
    {
        string masked = r.Redact(input);
        Assert.Contains("[REDACTED]", masked);
        Assert.DoesNotContain(secret, masked);
    }

    [Fact]
    public void Redactor_does_not_partially_match_username_inside_words()
    {
        var r = new LogRedactor("al", @"C:\Users\al");
        // "al" should not be replaced inside "balance"
        Assert.Equal("balance", r.Redact("balance"));
    }

    [Fact]
    public void Redactor_handles_null_and_empty()
    {
        var r = new LogRedactor("alice", @"C:\Users\alice");
        Assert.Equal("", r.Redact(null));
        Assert.Equal("", r.Redact(""));
    }

    [Fact]
    public void FormatEntry_is_single_line_valid_json_and_redacted()
    {
        var r = new LogRedactor("alice", @"C:\Users\alice");
        string line = ExecutionLog.FormatEntry(T0, "backup.copy", @"copied C:\Users\alice\notes.txt", r,
            new Dictionary<string, string?> { ["wifi"] = "key=Secret123" });

        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("alice", line);
        Assert.DoesNotContain("Secret123", line);

        using var doc = JsonDocument.Parse(line); // throws if not valid JSON
        var root = doc.RootElement;
        Assert.Equal("backup.copy", root.GetProperty("evt").GetString());
        Assert.Equal("key=[REDACTED]", root.GetProperty("data").GetProperty("wifi").GetString());
        Assert.Contains("%USERPROFILE%", root.GetProperty("msg").GetString());
    }

    [Fact]
    public void ExecutionLog_appends_one_line_per_call()
    {
        string path = Path.Combine(Path.GetTempPath(), "wck-test-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var log = new ExecutionLog(path, new LogRedactor(null, null));
            log.Append("a", "first");
            log.Append("b", "second");

            string[] lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            using var d0 = JsonDocument.Parse(lines[0]);
            using var d1 = JsonDocument.Parse(lines[1]);
            Assert.Equal("a", d0.RootElement.GetProperty("evt").GetString());
            Assert.Equal("b", d1.RootElement.GetProperty("evt").GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

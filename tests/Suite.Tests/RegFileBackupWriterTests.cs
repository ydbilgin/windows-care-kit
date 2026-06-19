using Microsoft.Win32;
using WindowsCareKit.Execution.Adapters;
using Xunit;
using CoreHive = WindowsCareKit.Core.Planning.RegistryHive;
using CoreView = WindowsCareKit.Core.Planning.RegistryView;

namespace WindowsCareKit.Tests;

/// <summary>The real <c>.reg</c> backup writer produces re-importable text from a live disposable test key.</summary>
public class RegFileBackupWriterTests
{
    private const string Root = @"Software\WindowsCareKit.Tests";

    [Fact]
    public void Writes_a_value_backup_with_the_standard_header_and_value()
    {
        string sub = Root + @"\bak-value-" + Guid.NewGuid().ToString("N");
        string regFile = Path.Combine(Path.GetTempPath(), "wck-bak-" + Guid.NewGuid().ToString("N") + ".reg");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(sub))
            {
                key!.SetValue("Name", "Acme Updater");
                key.SetValue("Count", 7, RegistryValueKind.DWord);
            }

            new RegFileBackupWriter().WriteBackup(regFile, CoreHive.CurrentUser, CoreView.Registry64, sub, "Name");

            string text = File.ReadAllText(regFile);
            Assert.StartsWith("Windows Registry Editor Version 5.00", text);
            Assert.Contains(@"[HKEY_CURRENT_USER\" + sub + "]", text);
            Assert.Contains("\"Name\"=\"Acme Updater\"", text);
            Assert.DoesNotContain("Count", text); // value backup captures only the named value
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            if (File.Exists(regFile)) File.Delete(regFile);
        }
    }

    [Fact]
    public void Writes_a_subtree_backup_with_a_dword_and_a_child_key()
    {
        string sub = Root + @"\bak-tree-" + Guid.NewGuid().ToString("N");
        string regFile = Path.Combine(Path.GetTempPath(), "wck-baktree-" + Guid.NewGuid().ToString("N") + ".reg");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(sub))
            {
                key!.SetValue("Flag", 1, RegistryValueKind.DWord);
                using var child = key.CreateSubKey("Child");
                child!.SetValue("Label", "hello");
            }

            new RegFileBackupWriter().WriteBackup(regFile, CoreHive.CurrentUser, CoreView.Registry64, sub, valueName: null);

            string text = File.ReadAllText(regFile);
            Assert.Contains(@"[HKEY_CURRENT_USER\" + sub + "]", text);
            Assert.Contains("\"Flag\"=dword:00000001", text);
            Assert.Contains(@"[HKEY_CURRENT_USER\" + sub + @"\Child]", text);
            Assert.Contains("\"Label\"=\"hello\"", text);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            if (File.Exists(regFile)) File.Delete(regFile);
        }
    }

    [Fact]
    public void Missing_target_still_writes_a_header_only_backup()
    {
        string sub = Root + @"\absent-" + Guid.NewGuid().ToString("N");
        string regFile = Path.Combine(Path.GetTempPath(), "wck-absent-" + Guid.NewGuid().ToString("N") + ".reg");
        try
        {
            new RegFileBackupWriter().WriteBackup(regFile, CoreHive.CurrentUser, CoreView.Registry64, sub, valueName: null);
            string text = File.ReadAllText(regFile);
            Assert.StartsWith("Windows Registry Editor Version 5.00", text);
        }
        finally
        {
            if (File.Exists(regFile)) File.Delete(regFile);
        }
    }

    [Fact]
    public void REG_SZ_with_newlines_or_nul_is_emitted_as_hex1_and_round_trips()
    {
        string sub = Root + @"\bak-hex1-" + Guid.NewGuid().ToString("N");
        string regFile = Path.Combine(Path.GetTempPath(), "wck-hex1-" + Guid.NewGuid().ToString("N") + ".reg");
        const string multiline = "line1\r\nline2\r\nend";
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(sub))
            {
                // A REG_SZ that legitimately carries CR/LF — the quoted form would corrupt it.
                key!.SetValue("Multi", multiline, RegistryValueKind.String);
            }

            new RegFileBackupWriter().WriteBackup(regFile, CoreHive.CurrentUser, CoreView.Registry64, sub, "Multi");

            string text = File.ReadAllText(regFile);
            // It must be the hex(1) form, NOT a quoted string that splits across lines.
            Assert.Contains("\"Multi\"=hex(1):", text);
            Assert.DoesNotContain("\"Multi\"=\"line1", text);

            // The hex bytes must be the exact UTF-16LE encoding of the value plus a terminating null,
            // so re-importing the .reg restores the value byte-for-byte.
            string hex = ExtractHex(text, "\"Multi\"=hex(1):");
            byte[] bytes = hex.Split(',').Select(b => Convert.ToByte(b, 16)).ToArray();
            byte[] expected = System.Text.Encoding.Unicode.GetBytes(multiline + "\0");
            Assert.Equal(expected, bytes);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            if (File.Exists(regFile)) File.Delete(regFile);
        }
    }

    /// <summary>Pull the comma-separated hex payload that follows <paramref name="prefix"/> up to end of line.</summary>
    private static string ExtractHex(string text, string prefix)
    {
        int start = text.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, "prefix not found: " + prefix);
        start += prefix.Length;
        int end = text.IndexOf('\r', start);
        if (end < 0) end = text.IndexOf('\n', start);
        if (end < 0) end = text.Length;
        return text[start..end].Trim();
    }

    [Theory]
    [InlineData(CoreHive.LocalMachine, "HKEY_LOCAL_MACHINE")]
    [InlineData(CoreHive.CurrentUser, "HKEY_CURRENT_USER")]
    [InlineData(CoreHive.ClassesRoot, "HKEY_CLASSES_ROOT")]
    [InlineData(CoreHive.Users, "HKEY_USERS")]
    [InlineData(CoreHive.CurrentConfig, "HKEY_CURRENT_CONFIG")]
    public void HivePrefix_maps_every_hive(CoreHive hive, string expected)
        => Assert.Equal(expected, RegFileBackupWriter.HivePrefix(hive));
}

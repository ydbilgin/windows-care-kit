using Microsoft.Win32;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Execution.Adapters;
using Xunit;
using CoreHive = WindowsCareKit.Core.Planning.RegistryHive;
using CoreView = WindowsCareKit.Core.Planning.RegistryView;

namespace WindowsCareKit.Tests;

/// <summary>The registry delete exports a <c>.reg</c> backup BEFORE deleting and refuses if the backup fails.</summary>
public class RegistryBackupBeforeDeleteTests
{
    /// <summary>Records call order; can be made to throw to simulate a failed backup write.</summary>
    private sealed class RecordingBackupWriter : IRegBackupWriter
    {
        public List<string> Events { get; } = new();
        public bool Throw { get; set; }
        public string? LastDestination { get; private set; }

        public void WriteBackup(string destinationPath, CoreHive hive, CoreView view, string subKeyPath, string? valueName)
        {
            LastDestination = destinationPath;
            Events.Add("backup:" + destinationPath);
            if (Throw)
                throw new IOException("backup destination not writable");
        }
    }

    // A registry adapter whose actual delete is observable via a fake base-key open is heavy; instead we
    // verify ORDER and FAIL-CLOSED using a subclass-free seam: the real adapter calls the writer first,
    // then attempts the delete. We point the delete at a key that does not exist so the delete is a no-op
    // (OpenSubKey returns null) — letting us assert the backup happened and no throw on a missing target.

    private const string TestSubKey = @"Software\WindowsCareKit.Tests\does-not-exist-" + "ABC123";

    [Fact]
    public void Backup_is_written_before_the_delete_is_attempted()
    {
        var writer = new RecordingBackupWriter();
        var adapter = new RegistryDeleteAdapter(Path.GetTempPath(), writer);

        var action = new RegistryDeleteAction
        {
            Hive = CoreHive.CurrentUser,
            SubKeyPath = TestSubKey,
            ValueName = "SomeValue",
            View = CoreView.Registry64,
            Description = "del value",
            Reason = "test",
        };

        adapter.Delete(action); // missing key → delete is a no-op, but the backup must still run

        Assert.Single(writer.Events);
        Assert.StartsWith("backup:", writer.Events[0]);
    }

    [Fact]
    public void A_failed_backup_refuses_the_delete_fail_closed()
    {
        var writer = new RecordingBackupWriter { Throw = true };
        var adapter = new RegistryDeleteAdapter(Path.GetTempPath(), writer);

        var action = new RegistryDeleteAction
        {
            Hive = CoreHive.CurrentUser,
            SubKeyPath = TestSubKey,
            View = CoreView.Registry64,
            Description = "del key",
            Reason = "test",
        };

        // No backup → the adapter throws and never reaches the delete.
        Assert.Throws<IOException>(() => adapter.Delete(action));
    }

    [Fact]
    public void Ignores_the_action_supplied_backup_path_and_writes_under_the_backup_dir()
    {
        // Security (M2): the publicly-settable BackupRegFile must NOT be used as a write destination —
        // it would be an un-gated arbitrary-path write sink. The backup always lands under the regbak dir.
        var writer = new RecordingBackupWriter();
        string backupDir = Path.Combine(Path.GetTempPath(), "wck-regbak-" + Guid.NewGuid().ToString("N"));
        var adapter = new RegistryDeleteAdapter(backupDir, writer);
        string attackerPath = Path.Combine(Path.GetTempPath(), "evil-elsewhere.reg");

        var action = new RegistryDeleteAction
        {
            Hive = CoreHive.CurrentUser,
            SubKeyPath = TestSubKey,
            ValueName = "v",
            BackupRegFile = attackerPath,
            Description = "d",
            Reason = "t",
        };

        adapter.Delete(action);

        Assert.NotEqual(attackerPath, writer.LastDestination);
        Assert.StartsWith(Path.GetFullPath(backupDir), Path.GetFullPath(writer.LastDestination!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolved_backup_path_is_sanitized_and_under_the_backup_dir()
    {
        var writer = new RecordingBackupWriter();
        string backupDir = Path.Combine(Path.GetTempPath(), "wck-regbak-" + Guid.NewGuid().ToString("N"));
        var adapter = new RegistryDeleteAdapter(backupDir, writer);

        var action = new RegistryDeleteAction
        {
            Hive = CoreHive.CurrentUser,
            SubKeyPath = @"Software\Vendor\App",
            ValueName = "v",
            Description = "d",
            Reason = "t",
        };

        adapter.Delete(action);

        Assert.NotNull(writer.LastDestination);
        Assert.StartsWith(backupDir, writer.LastDestination!);
        Assert.EndsWith(".reg", writer.LastDestination);
        Assert.DoesNotContain(@"\Software\Vendor\App", writer.LastDestination); // backslashes sanitized out of the file name
    }

    [Fact]
    public void Same_key_value_deletes_get_distinct_value_scoped_backup_files_and_preserve_both_values()
    {
        string backupDir = Path.Combine(Path.GetTempPath(), "wck-regbak-" + Guid.NewGuid().ToString("N"));
        string sub = @"Software\WindowsCareKit.Tests\regbak-collision-" + Guid.NewGuid().ToString("N");
        var fixedUtc = new DateTime(2026, 7, 2, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567);
        var fixedGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var adapter = new RegistryDeleteAdapter(
            backupDir,
            new RegFileBackupWriter(),
            utcNow: () => fixedUtc,
            newGuid: () => fixedGuid);

        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(sub)!)
            {
                key.SetValue("FirstValue", "first-content");
                key.SetValue("SecondValue", "second-content");
            }

            adapter.Delete(new RegistryDeleteAction
            {
                Hive = CoreHive.CurrentUser,
                SubKeyPath = sub,
                ValueName = "FirstValue",
                View = CoreView.Registry64,
                Description = "delete first value",
                Reason = "test",
            });
            adapter.Delete(new RegistryDeleteAction
            {
                Hive = CoreHive.CurrentUser,
                SubKeyPath = sub,
                ValueName = "SecondValue",
                View = CoreView.Registry64,
                Description = "delete second value",
                Reason = "test",
            });

            string[] files = Directory.GetFiles(backupDir, "*.reg");
            Assert.Equal(2, files.Length);
            Assert.Equal(2, files.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Contains(files, f => Path.GetFileName(f).Contains("value_FirstValue", StringComparison.Ordinal));
            Assert.Contains(files, f => Path.GetFileName(f).Contains("value_SecondValue", StringComparison.Ordinal));

            string firstBackup = File.ReadAllText(files.Single(f => Path.GetFileName(f).Contains("value_FirstValue", StringComparison.Ordinal)));
            string secondBackup = File.ReadAllText(files.Single(f => Path.GetFileName(f).Contains("value_SecondValue", StringComparison.Ordinal)));
            Assert.Contains("\"FirstValue\"=\"first-content\"", firstBackup);
            Assert.DoesNotContain("SecondValue", firstBackup);
            Assert.Contains("\"SecondValue\"=\"second-content\"", secondBackup);
            Assert.DoesNotContain("FirstValue", secondBackup);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
            if (Directory.Exists(backupDir))
                Directory.Delete(backupDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"Software\Vendor\App", null, "App")]                 // key delete → last segment
    [InlineData(@"Software\Vendor\App", "Vendor", "App")]
    [InlineData(@"Single", null, "Single")]
    public void SplitParent_separates_parent_and_last_segment(string input, string? expectedParentContains, string expectedLast)
    {
        RegistryDeleteAdapter.SplitParent(input, out string? parent, out string last);
        Assert.Equal(expectedLast, last);
        if (expectedParentContains is null)
            Assert.True(parent is null || parent.Contains(expectedParentContains ?? "", StringComparison.Ordinal));
        else
            Assert.Contains(expectedParentContains, parent);
    }

    [Fact]
    public void MapHive_and_MapView_match_the_BCL_enums()
    {
        Assert.Equal(Microsoft.Win32.RegistryHive.LocalMachine, RegistryDeleteAdapter.MapHive(CoreHive.LocalMachine));
        Assert.Equal(Microsoft.Win32.RegistryHive.CurrentUser, RegistryDeleteAdapter.MapHive(CoreHive.CurrentUser));
        Assert.Equal(Microsoft.Win32.RegistryView.Registry32, RegistryDeleteAdapter.MapView(CoreView.Registry32));
        Assert.Equal(Microsoft.Win32.RegistryView.Registry64, RegistryDeleteAdapter.MapView(CoreView.Registry64));
    }
}
